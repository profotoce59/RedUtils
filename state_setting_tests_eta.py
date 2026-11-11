"""
Banc d'etalonnage de l'ETA — "temps minimal pour aller de A a B".

C'est la brique de base du moteur de deplacement : tant que ce calcul est faux,
tout ce qui s'appuie dessus (choix de tir, possession, roles, dosage du boost)
est bati sur du sable.

Workflow :
    1. RedUtils/Fixes.cs : EtaBench = true
    2. dotnet build Bot.sln
    3. Lancer un match RLBot (state setting active), 4 voitures.
    4. python state_setting_tests_eta.py, puis derouler les scenarios un par un.

Chaque scenario place la voiture ET la balle. La balle ne sert que de REPERE :
le bot roule a fond vers elle et chronometre. Il imprime :

    [BENCH] DEPART dist=1500 angle=0deg v0=0 boost0=100 dodges=oui prevu=1.234s
    [BENCH] ARRIVE dist=1500 ... flips=1 prevu=1.234 reel=1.310 erreur=+0.076 (+6%) ...

    erreur > 0  -> ETA OPTIMISTE : le bot croit pouvoir faire ce qu'il ne peut pas.
                   C'est ce qui lui fait accepter des interceptions hors de portee.
    erreur < 0  -> ETA PESSIMISTE : il refuse des tirs pourtant jouables.

CANAL "SANS DODGE"
------------------
Les scenarios *_nododge font conduire le bot avec allowDodges=false. Le signal
passe par le BOOST DU COEQUIPIER gare (0 = dodges autorises, 100 = interdits) :
un flag C# imposerait de recompiler entre chaque course. Le coequipier est gare
dans un coin et n'influence pas notre trajet.

Comparer Dxxx et Dxxx_nododge a distance EGALE mesure directement le cout du
flip, au lieu de le deduire de l'ecart global. C'est ce qui permettra de
calibrer la penalite de flip dans Bot/Movement.cs.

CE QU'ON CHERCHE
----------------
Pas la valeur d'un cas isole, mais la FORME de l'erreur :
  - elle enfle avec la distance ?   -> courbe d'acceleration
  - elle explose avec l'angle ?     -> modele de virage
  - elle n'apparait qu'avec flips ? -> penalite de flip
Releve tout dans un tableau avant de conclure.

Nous = PLAYER_ORANGE1 (index 2). Notre but est en y = +5120, on attaque vers -y.
"""

import math

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
PLAYER_ORANGE1 = 2   # nous
PLAYER_ORANGE2 = 3   # gare — sert de canal "dodges autorises ?"

ONGROUNDHEIGHT = 17
BALLGROUND = 93      # rayon de la balle : elle repose au sol, immobile

YAWRIGHT = 0         # +x
YAW_ORANGE = 1.5708  # +y
YAW_LEFT = 3.14      # -x
YAW_BLUE = 4.71      # -y (orientation de depart par defaut)

# Point de depart commun. Choisi pour que TOUS les scenarios restent en terrain
# degage : 5000 uu droit devant amenent a y=-2500, et la cible a 180 deg tombe a
# y=4000, soit encore 1100 uu devant notre ligne de but (la geometrie du but
# fausserait la mesure).
START = (0.0, 2500.0)


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


def bench(car_xy, target_xy, yaw=YAW_BLUE, vel=(0, 0, 0), boost=100, dodges=True):
    """Voiture en car_xy (orientation yaw, vitesse vel, boost), cible en target_xy.

    dodges=False -> le coequipier recoit 100 de boost, ce que le bot lit comme
    "conduire sans dodge" (voir MyBot.BenchDodgesAllowed).
    """
    return GameState(
        ball=BallState(physics=Physics(
            location=Vector3(target_xy[0], target_xy[1], BALLGROUND),
            velocity=Vector3(0, 0, 0),
            angular_velocity=Vector3(0, 0, 0),
        )),
        cars={
            PLAYER_ORANGE1: CarState(
                physics=Physics(
                    location=Vector3(car_xy[0], car_xy[1], ONGROUNDHEIGHT),
                    rotation=Rotator(pitch=0, yaw=yaw, roll=0),
                    velocity=Vector3(*vel),
                    angular_velocity=Vector3(0, 0, 0),
                ),
                boost_amount=boost,
            ),
            PLAYER_ORANGE2: parked(3800, 4900, boost=0 if dodges else 100),
            PLAYER_BLUE1: parked(-3800, -4900),
            PLAYER_BLUE2: parked(-3500, -4900),
        },
    )


def straight(dist, **kw):
    """Cible a `dist` droit devant, la voiture etant orientee vers -y."""
    return bench(START, (START[0], START[1] - dist), **kw)


def at_angle(dist, degrees, **kw):
    """Cible a `dist`, `degrees` sur le cote (0 = droit devant, 180 = derriere).

    La voiture regarde toujours vers -y ; on fait tourner la CIBLE autour d'elle.
    """
    rad = math.radians(degrees)
    return bench(START,
                 (START[0] + dist * math.sin(rad), START[1] - dist * math.cos(rad)),
                 **kw)


TEST_STATES = [
    # ================= DISTANCE (droit devant, arret, boost 100) =================
    # Isole la courbe d'acceleration, et fait apparaitre le seuil a partir duquel
    # Drive decide de flipper (regarder la colonne flips=).
    ("D1   500 uu droit devant", straight(500)),
    ("D2  1000 uu droit devant", straight(1000)),
    ("D3  1500 uu droit devant", straight(1500)),
    ("D4  2000 uu droit devant", straight(2000)),
    ("D5  2500 uu droit devant", straight(2500)),
    ("D6  2500 uu droit devant", straight(3000)),

    # ================= LES MEMES, SANS DODGE =================
    # L'ecart avec D4..D8 a distance egale = cout reel du flip.
    ("N4  2000 uu droit devant, SANS DODGE", straight(2000, dodges=False)),
    ("N5  2500 uu droit devant, SANS DODGE", straight(2500, dodges=False)),
    ("N6  3000 uu droit devant, SANS DODGE", straight(3000, dodges=False)),
    ("N7  4000 uu droit devant, SANS DODGE", straight(4000, dodges=False)),
    ("N8  5000 uu droit devant, SANS DODGE", straight(5000, dodges=False)),

    # ================= ANGLE (1500 uu, arret, boost 100) =================
    # Etalonne le cout du virage sur une COURBE et non deux points.
    # Attention : plus l'angle est grand, plus Drive tarde a booster
    # (porte angleToTarget < 0.3), ce que le modele devra reproduire.
    ("A1  1500 uu a  30 deg", at_angle(1500, 30)),
    ("A2  1500 uu a  45 deg", at_angle(1500, 45)),
    ("A3  1500 uu a  60 deg", at_angle(1500, 60)),
    ("A4  1500 uu a  90 deg", at_angle(1500, 90)),
    ("A5  1500 uu a 135 deg", at_angle(1500, 135)),
    ("A6  1500 uu a 180 deg", at_angle(1500, 180)),

    # Meme serie sans dodge : separe le cout du virage de celui du flip.
    ("B4  1500 uu a  90 deg, SANS DODGE", at_angle(1500, 90, dodges=False)),
    ("B6  1500 uu a 180 deg, SANS DODGE", at_angle(1500, 180, dodges=False)),

    # ================= BOOST (2500 uu droit devant, arret) =================
    # L'ecart entre ces trois mesure ce que le modele credite au boost.
    ("C1  2500 uu, boost 0", straight(2500, boost=0)),
    ("C2  2500 uu, boost 30", straight(2500, boost=30)),
    ("C3  2500 uu, boost 100", straight(2500, boost=100)),

    # ================= VITESSE INITIALE (2500 uu droit devant) =================
    # Verifie que le modele ne re-accelere pas une voiture deja au plafond.
    ("V1  2500 uu, lance a 1000", straight(2500, vel=(0, -1000, 0))),
    ("V2  2500 uu, lance a 1400", straight(2500, vel=(0, -1400, 0))),
    ("V3  2500 uu, lance a 2300, boost 0", straight(2500, vel=(0, -2300, 0), boost=0)),
    # Pire cas : lance A L'ENVERS, il faut freiner ou faire demi-tour.
    ("V4  1500 uu, lance a 1400 EN ARRIERE", straight(1500, vel=(0, 1400, 0))),
]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} scenarios d'etalonnage ETA.\n")
    print("Rappel : Fixes.EtaBench doit etre a true (le bot ignore alors sa strategie).")
    print("Laisse chaque course FINIR (ligne ARRIVE) avant de lancer la suivante.\n")

    while True:
        for index, (name, _) in enumerate(TEST_STATES):
            print(f"  [{index:2}] {name}")
        choice = input("\nNumero du scenario (Entree pour quitter) : ").strip()
        if choice == "":
            break
        try:
            name, state = TEST_STATES[int(choice)]
        except (ValueError, IndexError):
            print("Choix invalide.\n")
            continue
        sm.game_interface.set_game_state(state)
        print(f"-> {name}\n")


if __name__ == "__main__":
    main()
