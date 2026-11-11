"""
Script de test de state setting pour RLBot.

Prerequis :
- Un match RLBot est deja lance (depuis RLBotGUI, avec "Enable State Setting" coche).
- Le package pip "rlbot" est installe dans l'environnement Python qui execute ce script.

Usage :
    python state_setting_tests.py

Ce script se connecte a la partie en cours et applique, un par un, les etats
definis dans TEST_STATES. Appuie sur Entree pour passer a l'etat suivant.
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


# Index du joueur humain/bot que tu veux teleporter (0 = premier joueur ajoute au match).
PLAYER_BLUE1 = 0 ##Bleu 1
PLAYER_BLUE2 = 1 ##Bleu 2
PLAYER_ORANGE1 = 2 ##Orange 1 Nous par défault
PLAYER_ORANGE2 = 3 ##Orange 2
ONGROUNDHEIGHT = 17
YAWRIGHT = 0
YAW_ORANGE = 1.5708
YAW_LEFT = 3.14
YAW_BLUE = 4.71

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
# Ajoute ou modifie les scenarios ici. Chaque entree est (nom, GameState).
TEST_STATES = [
    (
        "Balle en défense rebondissant près de toi (Shot)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, 3000, 500),
                velocity=Vector3(0, 0, -5),
                )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, 4000,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
                PLAYER_ORANGE2 : CarState(
                    physics=Physics(
                        location=Vector3(-400, -4608,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=1.5708, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
                PLAYER_BLUE1: CarState(
                    physics=Physics(
                        location=Vector3(-450, -4608,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=1.5708, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
                PLAYER_BLUE2: CarState(
                    physics=Physics(
                        location=Vector3(-450, -4200, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=1.5708, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
            },
        ),
    ),
    (
        "Balle en défense rebondissant près de toi (Controle)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, 3000, 500),
                velocity=Vector3(0, 0, -5),
                )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(800, 3000,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_LEFT, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
                PLAYER_ORANGE2 : CarState(
                    physics=Physics(
                        location=Vector3(-400, -4608,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=1.5708, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
                PLAYER_BLUE1: CarState(
                    physics=Physics(
                        location=Vector3(-450, -4608,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=1.5708, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
                PLAYER_BLUE2: CarState(
                    physics=Physics(
                        location=Vector3(-450, -4200, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=1.5708, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
            },
        ),
    ),
    (
        "50 50",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, 300, ONGROUNDHEIGHT),
                velocity=Vector3(0, 0, -5),
                )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, 1200,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=33,
                ),
                PLAYER_ORANGE2 : parked(-3000, 4600),
                PLAYER_BLUE1: CarState(
                    physics=Physics(
                        location=Vector3(-50, -500,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_ORANGE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=100,
                ),
                PLAYER_BLUE2: CarState(
                    physics=Physics(
                        location=Vector3(150, -500,ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_ORANGE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=100,
                ),
            },
        ),
    ),


]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} etats de test charges.\n")

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
