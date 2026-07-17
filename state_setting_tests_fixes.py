"""
State setters de test pour les correctifs du framework (Fixes.cs).

Workflow pour chaque fix :
    1. Ouvre RedUtils/Fixes.cs et regle le flag du fix a tester (true = corrige, false = comportement d'origine).
    2. Recompile : dotnet build Bot.sln
    3. Relance le match RLBot (avec "Enable State Setting" coche).
    4. Lance ce script et applique le scenario correspondant au fix.
    5. Compare le comportement avec flag true vs false (2 runs).

Prerequis :
- Un match RLBot deja lance, state setting active.
- Nous = PLAYER_ORANGE1 (index 2), comme dans state_setting_tests.py.

NOTE Fix #1 (Field.Initialize / boost pads dupliques) : pas testable en state setting.
Verification : lancer un match 2v2 avec DEUX RedBots et regarder la console du bot au demarrage.
    - Ligne affichee : "[RedUtils] Field initialized: N boost pads"
    - Avec fix  (FieldInitClearBoosts = true)  : toujours 34.
    - Sans fix  (false)                        : 68 quand le 2e bot rejoint,
      et en jeu le Support part parfois vers un gros pad deja pris (pad "fantome").

NOTE Fix #5 (AerialShot.IsValid) : fix de robustesse, AUCUNE difference de comportement attendue.
Le scenario sert de test de non-regression : l'aerial doit se declencher a l'identique avec et sans.
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

# Index des joueurs (identiques a state_setting_tests.py)
PLAYER_BLUE1 = 0    # Bleu 1
PLAYER_BLUE2 = 1    # Bleu 2
PLAYER_ORANGE1 = 2  # Orange 1 — Nous par defaut
PLAYER_ORANGE2 = 3  # Orange 2

ONGROUNDHEIGHT = 17
YAWRIGHT = 0        # +x
YAW_ORANGE = 1.5708 # +y (vers le but orange)
YAW_LEFT = 3.14     # -x
YAW_BLUE = 4.71     # -y (vers le but bleu)

# Voitures "gares" hors de l'action pour ne pas polluer le test
# (loin de la balle -> notre bot reste Attacker, et l'ETA adverse reste grande)
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
    # FIX #2 — JumpShot mural (Fixes.JumpShotGravityFix)
    # La balle longe le mur droit a ~250 uu de la surface, a hauteur de jump shot mural.
    # Notre voiture est deja sur le mur droit, orientee vers la balle.
    # ATTENDU avec fix   : le bot saute du mur au bon moment et touche la balle.
    # ATTENDU sans fix   : le saut est mal synchronise (la compensation de gravite
    #                      vaut 0), le bot saute trop tot/tard et manque la balle.
    # NB : si la voiture apparait mal orientee sur le mur, inverse le signe du roll.
    # ------------------------------------------------------------------
    (
        "FIX2 - Jump shot mural (balle longeant le mur droit)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(3850, 800, 900),
                velocity=Vector3(0, -900, 400),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        # Sur le mur droit (x ~ 4080), roues contre le mur, face a -y
                        location=Vector3(4080, 1800, 500),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=1.5708),
                        velocity=Vector3(0, -400, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=30,
                ),
                PLAYER_ORANGE2: parked(-3000, 4600),
                PLAYER_BLUE1: parked(-3400, 4600),
                PLAYER_BLUE2: parked(-3700, 4600),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # FIX #3 — Puissance de tir (Fixes.ShotPowerModifierFix)
    # Balle rapide venant vers nous (vitesse relative elevee a l'impact > 2300 uu/s).
    # Le bot doit contrer vers le but bleu (y = -5120).
    # ATTENDU avec fix   : la visee compense correctement la puissance reelle,
    #                      le contre part cadre (répeter 3-4 fois : plus de tirs cadres).
    # ATTENDU sans fix   : la puissance est surestimee -> direction de tir legerement
    #                      fausse, contres decadres / a cote du but.
    # ------------------------------------------------------------------
    (
        "FIX3 - Contre puissant sur balle rapide",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, -2000, 200),
                velocity=Vector3(0, 2000, 300),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, 2500, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, -500, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=20,
                ),
                PLAYER_ORANGE2: parked(3500, 4600),
                PLAYER_BLUE1: parked(-3400, -4600, yaw=YAW_BLUE),
                PLAYER_BLUE2: parked(-3700, -4600, yaw=YAW_BLUE),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # FIX #4 — Fin de prediction (Fixes.BallPredictionFindFix)
    # Balle lobee tres haut retombant loin de nous ; bot sans boost a l'autre bout.
    # La premiere slice atteignable tombe tout en fin d'horizon de prediction (~6 s).
    # ATTENDU avec fix   : le bot s'engage vers l'interception des que possible
    #                      (regarde l'overlay debug : STATE/ACTION se mettent a jour).
    # ATTENDU sans fix   : le bot ignore les slices du dernier bloc -> il reste
    #                      passif / STATE incoherent une fraction de seconde de plus,
    #                      ou considere la balle inatteignable alors qu'elle l'est de justesse.
    # NB : difference SUBTILE — observe l'overlay debug (STATE / ACTION) plutot que
    #      le mouvement brut, et rejoue le scenario plusieurs fois.
    # ------------------------------------------------------------------
    (
        "FIX4 - Balle lobee retombant en fin de prediction",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(-2000, -3000, 300),
                velocity=Vector3(300, 500, 1400),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(3000, 4500, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=0,
                ),
                PLAYER_ORANGE2: parked(3800, 4900),
                PLAYER_BLUE1: parked(-3400, -4900, yaw=YAW_ORANGE),
                PLAYER_BLUE2: parked(-3700, -4900, yaw=YAW_ORANGE),
            },
        ),
    ),
]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} etats de test charges.\n")
    print("Rappel : pour comparer avec/sans fix, change le flag dans Fixes.cs,")
    print("recompile (dotnet build Bot.sln) et relance le match entre les deux runs.\n")

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
