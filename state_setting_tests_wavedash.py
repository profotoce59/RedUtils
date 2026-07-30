"""
Banc de mesure du WAVEDASH — que rapporte reellement une manoeuvre de wavedash ?

On veut calibrer le remplacement Dodge -> Wavedash dans Drive.cs (branche sol) :
combien de vitesse gagne-t-on, combien de boost ca coute, et combien de temps la
voiture est immobilisee (non-disponibilite : pendant le wavedash, impossible d'en
relancer un autre).

Workflow :
    1. RedUtils/Fixes.cs : WavedashBench = true
    2. dotnet build Bot.sln
    3. Lancer un match RLBot (state setting actif), 4 voitures.
    4. python state_setting_tests_wavedash.py, puis derouler les scenarios un par un.

Chaque scenario impose une VITESSE INITIALE a la voiture (droit devant, vers -y), puis
declenche UN wavedash. La mesure s'arrete PILE a l'atterrissage : on ne mesure que le
wavedash lui-meme. Le bot imprime :

    [WDBENCH] DEPART mode=wavedash v0=1000 boost0=100
    [WDBENCH] FIN wavedash v0=1000 vFin=1350 gain=+350 vPic=1400 boostUtilise=0 duree=1.03s (...) dist=800

Lecture de la ligne FIN wavedash :
  - vFin / gain -> vitesse a l'atterrissage et gain net apporte par le wavedash.
  - vPic        -> pic de vitesse pendant la manoeuvre.
  - boostUtilise-> doit rester ~0 (le wavedash ne booste pas).
  - duree       -> temps de NON-DISPONIBILITE (saut -> air -> dodge -> au sol). Drive.cs
                   impose +0.2 s de timeOnGround en plus avant de relancer un flip.
  - dist        -> distance parcourue PENDANT la manoeuvre (depart -> atterrissage).

Un "TIMEOUT ... wavedash au sol casse ?" signale que la voiture n'a jamais atterri.

REFERENCE (scenarios R*) : conduite classique (throttle seul), mesuree sur la duree
NOMINALE d'un wavedash (~1.0 s). Comparer, a v0 EGALE, dist(wavedash) vs dist(REFERENCE) :
si le wavedash parcourt MOINS, il fait perdre du terrain a cette vitesse (attendu a
basse vitesse). Le v0 ou ca bascule = le seuil a partir duquel il vaut le coup.

Nous = PLAYER_ORANGE1 (index 2). On roule vers -y, terrain degage devant.
Les 4 voitures tournent le meme bot, mais seule l'index 2 imprime [WDBENCH]
(filtre WavedashBenchCarIndex cote C#) : une seule ligne DEPART/FIN par scenario.
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
PLAYER_ORANGE1 = 2   # nous
PLAYER_ORANGE2 = 3   # gare, hors du chemin

ONGROUNDHEIGHT = 17
BALLGROUND = 93

YAW_BLUE = 4.71      # -y : orientation de depart (on fonce vers -y)

# Depart haut sur le terrain : a 2300 uu/s pendant ~1 s le wavedash reste en zone
# degagee (de y=3000 vers y=700), sans toucher la ligne de but ni les murs.
START = (0.0, 3000.0)


def parked(x, y, boost=0):
    return CarState(
        physics=Physics(
            location=Vector3(x, y, ONGROUNDHEIGHT),
            rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
            velocity=Vector3(0, 0, 0),
            angular_velocity=Vector3(0, 0, 0),
        ),
        boost_amount=boost,
    )


def wd(v0, boost=100, reference=False, ref_boost=0.0):
    """Voiture au depart, orientee vers -y, lancee a v0 droit devant.

    La balle est garee loin derriere : elle n'intervient pas (le banc declenche le
    wavedash directement, sans viser la balle).

    reference=True -> conduite classique (PAS de wavedash) : throttle seul sur la meme
    fenetre de 1s. Le temps de boost de la reference est encode dans le boost du
    coequipier gare (ORANGE2) : 50 = 0s (throttle pur), puis (boost-50)/100 secondes.
    ref_boost = temps de boost voulu (s) -> ORANGE2 recoit 50 + round(ref_boost*100).
    Comparer dist(reference+boost) a la variante BOOSTEE du wavedash a v0 egale.
    """
    return GameState(
        ball=BallState(physics=Physics(
            location=Vector3(0, 0, BALLGROUND),
            velocity=Vector3(0, 0, 0),
            angular_velocity=Vector3(0, 0, 0),
        )),
        cars={
            PLAYER_ORANGE1: CarState(
                physics=Physics(
                    location=Vector3(START[0], START[1]+200, ONGROUNDHEIGHT),
                    rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                    velocity=Vector3(0, -v0, 0),
                    angular_velocity=Vector3(0, 0, 0),
                ),
                boost_amount=boost,
            ),
            PLAYER_ORANGE2: parked(3800, 4900, boost=(50 + round(ref_boost * 100)) if reference else 0),
            PLAYER_BLUE1: parked(-3800, -4900),
            PLAYER_BLUE2: parked(-3500, -4900),
        },
    )


TEST_STATES = [
    # ===== GAIN DE VITESSE SELON v0 (boost plein, jamais consomme) =====
    # Isole ce que le wavedash ajoute. Attendu : fort a l'arret, decroissant, puis
    # nul/negatif quand on approche du plafond (2300).
    ("W0  wavedash a l'arret (v0=0)",    wd(0)),
    ("W1  wavedash a v0=500",            wd(500)),
    ("W2  wavedash a v0=1000",           wd(1000)),
    ("W3  wavedash a v0=1500",           wd(1500)),
    ("W4  wavedash a v0=2000",           wd(2000)),
    ("W5  wavedash a v0=2300 (plafond)", wd(2300)),

    # ===== REFERENCE : CONDUITE CLASSIQUE (throttle seul, meme fenetre) =====
    # A comparer paire a paire avec W0..W5 a v0 EGALE. Regarder la colonne dist= :
    # si le wavedash parcourt MOINS que la reference, il est contre-productif a cette
    # vitesse (cas attendu a basse vitesse). Le seuil ou dist(wavedash) > dist(ref)
    # donne la vitesse a partir de laquelle le wavedash vaut le coup.
    ("R0  REFERENCE conduite classique v0=0",    wd(0,    reference=True)),
    ("R1  REFERENCE conduite classique v0=500", wd(500, reference=True)),
    ("R2  REFERENCE conduite classique v0=1000", wd(1000, reference=True)),
    ("R3  REFERENCE conduite classique v0=1500", wd(1500, reference=True)),
    ("R4  REFERENCE conduite classique v0=2000", wd(2000, reference=True)),
    ("R5  REFERENCE conduite classique v0=2300", wd(2300, reference=True)),

    # ===== REFERENCE + BOOST (avance 1s en boostant X secondes) =====
    # Pour comparer a la variante BOOSTEE du wavedash. Meme fenetre 1s, throttle plein,
    # mais on boost les X premieres secondes. Regarder dist= (terrain gagne) et
    # boostUtilise= (cout). A comparer a la ligne FIN wavedash de la variante boostee.
    ("RB0  v0=0, boost 0.10s", wd(0, reference=True, ref_boost=0.10)),
    ("RB1  v0=0, boost 0.20s", wd(0, reference=True, ref_boost=0.20)),
    ("RB2  v0=0, boost 0.25s", wd(0, reference=True, ref_boost=0.25)),
    ("RB3  v0=1000, boost 0.10", wd(1000, reference=True, ref_boost=0.10)),
    ("RB4  v0=1000, boost 0.20", wd(1000, reference=True, ref_boost=0.20)),
    ("RB5  v0=1000, boost 0.25s", wd(1000, reference=True, ref_boost=0.25)),

    # ===== CONFIRMATION BOOST =====
    # Meme course sans boost du tout : boostUtilise doit rester 0 dans les deux cas.
    ("Z1  v0=1000, boost 0", wd(1000, boost=0)),
    
]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} scenarios de mesure du wavedash.\n")
    print("Rappel : Fixes.WavedashBench doit etre a true (le bot ignore alors sa strategie).")
    print("Laisse chaque manoeuvre FINIR (ligne FIN) avant de lancer la suivante.\n")

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
