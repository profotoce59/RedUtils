"""
State setters de test pour la refonte defensive (Fixes.DefensiveOverhaul).

Workflow :
    1. Regle Fixes.DefensiveOverhaul dans RedUtils/Fixes.cs (true = nouvelle defense, false = origine).
    2. Recompile : dotnet build Bot.sln
    3. Relance le match RLBot (state setting active).
    4. Lance ce script, applique un scenario, repete-le 5 fois, note buts encaisses / CSC.
    5. Compare true vs false.

Quoi observer (overlay INTENT + logs console) :
    - "Shot→Save" / "Drive→Save"   : tir cadre detecte, le bot tente la save
    - "Shot→Dégagement"            : balle dangereuse degagee loin du but
    - "Drive→GoalSide"             : le bot refuse le contact mal place et se replie
      entre la balle et son but AVANT de challenger (c'est l'anti-CSC)
Sans le flag, tu verras "Fifty" / "Drive→Balle" dans les memes situations.

Nous = PLAYER_ORANGE1 (index 2), notre but en y = +5120.
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
    # DEF1 — Tir cadre puissant depuis le milieu de terrain.
    # La balle file droit dans notre but (y+). Le bot est pres du poteau.
    # AVEC flag : INTENT passe a Shot→Save / Drive→Save, le bot coupe la
    #             trajectoire et degage loin du but.
    # SANS flag : Fifty / Drive→Balle tardif, but encaisse ou touche molle.
    # ------------------------------------------------------------------
    (
        "DEF1 - Tir cadre puissant (save attendue)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, 0, 300),
                velocity=Vector3(0, 1900, 350),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(900, 4300, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=40,
                ),
                PLAYER_ORANGE2: parked(3500, 4800),
                PLAYER_BLUE1: parked(-3400, -4800),
                PLAYER_BLUE2: parked(-3700, -4800),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # DEF2 — Reproduction CSC : bot MAL PLACE, au-dessus de la balle.
    # La balle roule vers notre but, le bot la poursuit depuis le milieu
    # (il n'est PAS entre la balle et son but).
    # AVEC flag : INTENT = Drive→GoalSide — il contourne, se met goal-side,
    #             PUIS degage vers le camp adverse.
    # SANS flag : il fonce dans la balle par derriere (Drive→Balle/Fifty)
    #             et la pousse vers/dans son propre but.
    # ------------------------------------------------------------------
    (
        "DEF2 - Bot mal place derriere la balle (anti-CSC)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, 2600, 93),
                velocity=Vector3(0, 900, 0),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, 1300, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_ORANGE, roll=0),
                        velocity=Vector3(0, 800, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=50,
                ),
                PLAYER_ORANGE2: parked(3500, 4800),
                PLAYER_BLUE1: parked(-3400, -4800),
                PLAYER_BLUE2: parked(-3700, -4800),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # DEF3 — Centre-tir croise depuis l'aile vers notre surface.
    # Balle rapide en diagonale vers notre but, bot au second poteau.
    # AVEC flag : save/degagement vers le mur oppose a la balle.
    # SANS flag : touche non maitrisee vers le centre (danger CSC) ou rien.
    # ------------------------------------------------------------------
    (
        "DEF3 - Centre-tir croise dans notre surface",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(-3000, 1800, 350),
                velocity=Vector3(1300, 1400, 150),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(1600, 4200, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_LEFT, roll=0),
                        velocity=Vector3(0, 0, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=45,
                ),
                PLAYER_ORANGE2: parked(3500, 4800),
                PLAYER_BLUE1: parked(-3400, -4800),
                PLAYER_BLUE2: parked(-3700, -4800),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # DEF4 — Lob cadre au-dessus du bot.
    # Balle haute retombant dans notre but, bot sur la ligne, plein boost.
    # AVEC flag : tentative de save aerienne (Shot→Save via AerialShot/JumpShot),
    #             ou repli sur la trajectoire (Drive→Save).
    # SANS flag : le bot attend la balle au sol -> trop tard.
    # ------------------------------------------------------------------
    (
        "DEF4 - Lob cadre (save aerienne attendue)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, 1200, 1100),
                velocity=Vector3(0, 1250, 320),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, 4900, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=100,
                ),
                PLAYER_ORANGE2: parked(3500, 4800),
                PLAYER_BLUE1: parked(-3400, -4800),
                PLAYER_BLUE2: parked(-3700, -4800),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # DEF5 — CONTROLE : balle lente NON dangereuse dans notre camp.
    # Balle qui traverse lentement en largeur, loin du but.
    # ATTENDU (avec ET sans flag) : PAS de panique — pas de Shot→Save,
    # le bot joue normalement (dribble/contour/possession).
    # Si le bot degage cette balle en catastrophe avec le flag, la detection
    # de danger est trop sensible -> me le signaler.
    # ------------------------------------------------------------------
    (
        "DEF5 - Controle : balle lente non dangereuse",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(-2500, 2200, 93),
                velocity=Vector3(700, 0, 0),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, 3800, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_LEFT, roll=0),
                        velocity=Vector3(0, 0, 0),
                        angular_velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=60,
                ),
                PLAYER_ORANGE2: parked(3500, 4800),
                PLAYER_BLUE1: parked(-3400, -4800),
                PLAYER_BLUE2: parked(-3700, -4800),
            },
        ),
    ),
]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} etats de test charges.\n")
    print("Rappel : compare Fixes.DefensiveOverhaul = true vs false")
    print("(dotnet build Bot.sln + relance du match entre les deux runs).\n")

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
