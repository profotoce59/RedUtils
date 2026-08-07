"""
State setters de test pour le BUG « le bot passe SOUS la balle qui rebondit ».

Constat (logs du 366-374s) : apres le kickoff, un bot va chercher du boost,
l'autre devient Attacker sur une balle qui REBONDIT haut vers le milieu du terrain
(ballZ ~ 370..510). Au lieu de se placer sous le point de chute et de SAUTER pour
la toucher, il fait "Drive->Balle" a pleine vitesse (wasteBoost) et passe DESSOUS.

Cause (RedUtils/Bot/Bot.cs) :
    - En Contested/zone Defensive (l.1087-1098) et Possessed/zone Offensive
      (l.1101-1118), quand FindShot ne rend rien on retombe sur :
          SetDrive(Ball.Location, "Drive->Balle", allowDodges: false, wasteBoost: true)
      Or Drive traite la cible EN PLAT (FlatDist / FlatDirection, Drive.cs) : il
      ignore le z et fonce vers la projection au sol d'une balle qui est EN L'AIR.
      -> le bot arrive sous la balle pendant qu'elle est encore haute = il passe dessous.
    - Le seul truc qui saute, le Fifty, n'est arme que si une slice de prediction est
      a < 400 uu (3D) du bot ET arrive dans < 1.5 s (Bot.cs l.1089-1091, et le meme
      test dans Fifty.cs l.65-68). Balle haute = aucune slice proche en 3D dans la
      demi-seconde qui vient -> ballEta repasse au-dessus de 1.5 s -> on retombe sur
      Drive->Balle, et le Fifty deja arme s'auto-abandonne (AbandonEta). D'ou le
      clignotement Fifty <-> Drive->Balle observe (373.4s Fifty, 373.7s Drive->Balle).

Quoi observer (overlay INTENT + logs console) :
    BUG (ce qu'on veut reproduire) :
    - "Drive->Balle" alors que ballZ est haut (> ~300) : le bot fonce au sol sous la
      balle, la depasse (dist bot->balle qui remonte apres etre passe sous 700), sans
      jamais sauter.
    - Clignotement "Fifty" <-> "Drive->Balle" d'un tick a l'autre.
    ATTENDU (le bon comportement a terme) :
    - le bot se place sous le point de chute predit et SAUTE (Fifty/Jump) pour toucher
      la balle en l'air, au lieu de la traverser au sol.

Nous = PLAYER_ORANGE1 (index 2), notre but en y = +5120.
On attaque vers -y ; LEUR but (bleu) est en y = -5120. Notre moitie = y > 0.

Workflow :
    1. Lancer un match RLBot 2v2 avec MyBot (state setting actif).
    2. Lancer ce script, choisir un scenario, observer l'overlay INTENT + les logs.
       (Fixes.DebugShot = true imprime la pose exacte au moment du state set.)
"""

from rlbot.setup_manager import SetupManager
from rlbot.utils.game_state_util import (
    GameState,
    BallState,
    CarState,
    Physics,
    Vector3,
    Rotator,
)

PLAYER_BLUE1 = 0
PLAYER_BLUE2 = 1
PLAYER_ORANGE1 = 2  # Nous
PLAYER_ORANGE2 = 3

ONGROUNDHEIGHT = 17
YAW_ORANGE = 1.5708  # +y (vers notre but)
YAW_BLUE = 4.71      # -y (vers leur but = notre cap d'attaque)


def parked(x, y, yaw=YAW_ORANGE, boost=0):
    return CarState(
        physics=Physics(
            location=Vector3(x, y, ONGROUNDHEIGHT),
            rotation=Rotator(pitch=0, yaw=yaw, roll=0),
            velocity=Vector3(0, 0, 0),
            angular_velocity=Vector3(0, 0, 0),
        ),
        boost_amount=boost,
    )


TEST_STATES = [
    # ------------------------------------------------------------------
    # BOUNCE1 — Reproduction directe des logs.
    # Balle qui rebondit haut au milieu (legerement notre cote), montante,
    # derive lente vers leur but. On arrive lance depuis notre moitie, boost
    # moyen. Un adversaire conteste (etat proche de Contested). Coequipier
    # goal-side derriere -> on est l'Attacker.
    # ATTENDU (bug) : "Drive->Balle" pendant que ballZ est haut, le bot passe
    # dessous ; clignotement avec "Fifty".
    # ------------------------------------------------------------------
    (
        "BOUNCE1 - Rebond milieu, le bot passe dessous (repro logs)",
        GameState(
            ball=BallState(physics=Physics(
                # z=320 montant a vz=480 : pic ~z=490 vers t=0.74s, retombe a
                # z=320 vers t=1.5s -> la balle reste HAUTE tout le temps que le
                # bot met a la rejoindre.
                location=Vector3(150, 500, 320),
                velocity=Vector3(0, -140, 480),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(  # Nous, Attacker, lance vers la balle
                    physics=Physics(
                        location=Vector3(150, 2600, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, -1500, 0),
                    ),
                    boost_amount=30,
                ),
                # Coequipier goal-side (derriere nous) : couvre -> nous = Attacker.
                PLAYER_ORANGE2: parked(700, 4200),
                # Adversaire qui conteste, ~equidistant de la balle -> Contested.
                PLAYER_BLUE1: CarState(
                    physics=Physics(
                        location=Vector3(300, -300, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_ORANGE, roll=0),
                        velocity=Vector3(0, 500, 0),
                    ),
                    boost_amount=40,
                ),
                PLAYER_BLUE2: parked(-800, -4600),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # BOUNCE2 — Rebond franc plus HAUT, quasi sur place au milieu.
    # Isole le probleme de hauteur : la balle monte a ~z=650, le bot doit
    # attendre/se placer et sauter, pas foncer au sol.
    # ------------------------------------------------------------------
    (
        "BOUNCE2 - Rebond haut sur place au milieu",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, 300, 400),
                velocity=Vector3(0, -60, 650),   # pic ~z=725
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, 2300, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, -1200, 0),
                    ),
                    boost_amount=45,
                ),
                PLAYER_ORANGE2: parked(600, 4300),
                PLAYER_BLUE1: CarState(
                    physics=Physics(
                        location=Vector3(-200, -400, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_ORANGE, roll=0),
                        velocity=Vector3(0, 400, 0),
                    ),
                    boost_amount=40,
                ),
                PLAYER_BLUE2: parked(900, -4600),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # BOUNCE3 — Meme rebond, cote OFFENSIF (balle dans leur moitie, y<0).
    # Verifie que le meme "Drive->Balle" sous la balle se produit aussi via
    # la branche Possessed/Offensive (Bot.cs l.1118).
    # ------------------------------------------------------------------
    (
        "BOUNCE3 - Rebond haut cote offensif (passe dessous aussi)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(100, -600, 350),
                velocity=Vector3(0, -120, 520),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(100, 1400, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, -1500, 0),
                    ),
                    boost_amount=50,
                ),
                PLAYER_ORANGE2: parked(700, 3600),
                PLAYER_BLUE1: parked(-400, -3800),
                PLAYER_BLUE2: parked(1200, -4600),
            },
        ),
    ),
]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} etats de test charges.\n")
    print("But : reproduire le bot qui passe SOUS une balle qui rebondit au milieu.")
    print("Observer l'overlay INTENT : 'Drive->Balle' avec ballZ haut = bug.\n")

    while True:
        for index, (name, _) in enumerate(TEST_STATES):
            print(f"  [{index}] {name}")
        choice = input("\nNumero du scenario a appliquer (Entree pour quitter) : ").strip()
        if choice == "":
            break
        try:
            name, state = TEST_STATES[int(choice)]
        except (ValueError, IndexError):
            print("Choix invalide.\n")
            continue
        sm.game_interface.set_game_state(state)
        print(f"-> Etat applique : {name}\n")


if __name__ == "__main__":
    main()
