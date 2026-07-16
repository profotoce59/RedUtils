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


]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} etats de test charges.\n")

    for name, state in TEST_STATES:
        input(f"Appuie sur Entree pour appliquer : {name}")
        sm.game_interface.set_game_state(state)
        print(f"-> Etat applique : {name}\n")

    print("Tous les etats ont ete testes.")


if __name__ == "__main__":
    main()
