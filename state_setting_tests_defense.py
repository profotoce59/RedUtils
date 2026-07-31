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
    (
        # BUG observe : l'adversaire dribble la balle vers NOTRE but (y=+5120), nous
        # (ORANGE1) sommes goal-side, juste en face de lui, a ~300u de la balle. La
        # prediction extrapole la balle droit dans la cage -> priorite 1 de
        # TryDefensivePriority (goalSlice != null) -> "Drive->Save" : on RECULE vers le
        # but au lieu de contester. Attendu apres correctif : "Fifty" (challenge 50/50),
        # puisque oppDist ~= notre dist et qu'on est goal-side (challenge sain).
        #
        # A observer (overlay INTENT + log console) :
        #   - AVANT : intent=Drive->Save, dist ~= oppDist ~= 200-300, ballV ~2000
        #   - APRES : intent=Fifty
        # Note : blue1 ne "dribblera" vraiment que si c'est un bot qui dribble ; mais la
        # DECISION du bot se prend sur la geometrie de cet instant, deja reproduite ici.
        "Dribble adverse vers notre but — 50/50 goal-side (doit challenger, pas Drive->Save)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(-1200, 4000, 93),
                velocity=Vector3(0, 2000, 0),   # fonce vers notre but (+y)
                )),
            cars={
                # Nous : goal-side (y plus grand = plus pres de notre but), face a la balle (-y)
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(-1200, 4300, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=0,
                ),
                # Coequipier vivant (pour que le role soit calcule) mais loin -> nous = Attacker
                PLAYER_ORANGE2: parked(3000, 4800),
                # L'adversaire qui porte la balle : juste derriere elle, meme cap (+y), lance
                PLAYER_BLUE1: CarState(
                    physics=Physics(
                        location=Vector3(-1200, 3650, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_ORANGE, roll=0),
                        velocity=Vector3(0, 1400, 0),
                    ),
                    boost_amount=50,
                ),
                # Second adversaire loin, hors du coup
                PLAYER_BLUE2: parked(-3000, -4600, yaw=YAW_ORANGE),
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
