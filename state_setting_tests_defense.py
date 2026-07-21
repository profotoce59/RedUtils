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
