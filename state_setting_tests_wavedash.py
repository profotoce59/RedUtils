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

ANGLE DEMANDE (scenarios N*) : jusqu'ici le banc passait toujours Me.Forward, donc il ne
mesurait QUE le wavedash parfaitement aligne — alors qu'en jeu Drive le demande vers une
CIBLE, souvent de biais. C'est la que la voiture derive. Le canal ANGLE passe par le boost
du premier adversaire gare (50 = 0 deg, pas de 2 deg). Deux colonnes en plus dans FIN :
  - derive= : deviation LATERALE (uu) par rapport a la direction demandee (signe = cote) ;
  - capFin= : ecart (deg) entre direction demandee et vitesse reelle a l'arrivee.
Chercher l'angle au-dela duquel derive/capFin explosent : c'est la limite au-dela de
laquelle Drive ne devrait plus demander de wavedash.

ENCHAINEMENT (scenarios X*) : le banc ne faisait qu'UN wavedash, donc il ne pouvait pas
montrer le 2e qui degenere en front flip. Le canal CHAINE passe par le boost du second
adversaire gare (boost = 10 x nombre de dashes). Les dashes sont relances des que la
voiture est posee, avec le MEME delai que Drive (timeOnGround > 0.02s) : on mesure donc
l'enchainement REEL, pas une version confortable. Sortie :

    [WDBENCH] DEPART mode=wavedash v0=800 boost0=0 angle=+0 chaine=3
    [WDBENCH] DASH 1/3 v0=800 vFin=1250 gain=+450 duree=0.97s dist=980 derive=+12 capFin=2
    [WDBENCH] DASH 2/3 v0=1250 vFin=1600 gain=+350 duree=0.99s dist=1400 derive=+45 capFin=5
    [WDBENCH] DASH 3/3 v0=1600 vFin=1850 gain=+250 duree=1.02s dist=1700 derive=+180 capFin=19
    [WDBENCH] FIN CHAINE x3 ... (cumul depuis le depart)

Une ligne DASH par wavedash : c'est en les COMPARANT entre eux qu'on voit lequel casse.
Un gain qui decroit doucement est normal (plafond de vitesse) ; c'est la rupture brutale
de gain, duree ou derive qui signale le bug.

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


def wd(v0, boost=100, reference=False, ref_boost=0.0, angle=0, chain=1, relaunch=0.10):
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
            # ORANGE2 porte DEUX canaux selon le mode :
            #   reference=True  -> 50 + temps de boost de la reference (>= 50 signale le mode)
            #   reference=False -> delai de relance de la chaine, en centiemes de seconde (< 50)
            PLAYER_ORANGE2: parked(3800, 4900,
                                   boost=(50 + round(ref_boost * 100)) if reference
                                   else min(49, round(relaunch * 100))),
            # Canal ANGLE : boost du premier adversaire gare. 50 = 0 deg, pas de 2 deg.
            PLAYER_BLUE1: parked(-3800, -4900, boost=50 + round(angle / 2)),
            # Canal CHAINE : boost du second adversaire gare. boost = 10 x nombre de wavedashes.
            PLAYER_BLUE2: parked(-3500, -4900, boost=10 * chain),
        },
    )


def pose(location, rotation, velocity, angular_velocity, boost=100, angle=0, chain=1):
    """Rejoue UNE pose exacte, copiee d'une ligne [WDBENCH] POSE dashN.

    Sert a reproduire isolement le dash n2 d'une chaine : il ne demarre PAS d'un arret
    propre mais d'un atterrissage, avec vitesse verticale ET vitesse angulaire residuelles.
    C'est cette derniere qui fait monter le nez au lieu de le piquer, et aucun scenario
    "voiture posee a plat" ne peut la reproduire.

    Mode d'emploi :
      1. Lancer X1 (chaine x3), relever la ligne [WDBENCH] POSE dash2 dans la console.
      2. Copier ses 4 tuples ici, en scenario P*.
      3. Rejouer ce scenario seul : le dash echoue de la meme facon, mais isole et
         reproductible — on peut alors faire varier UN parametre a la fois.

    location/velocity/angular_velocity : tuples (x, y, z). rotation : (pitch, yaw, roll) rad.
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
                    location=Vector3(*location),
                    rotation=Rotator(*rotation),
                    velocity=Vector3(*velocity),
                    angular_velocity=Vector3(*angular_velocity),
                ),
                boost_amount=boost,
            ),
            PLAYER_ORANGE2: parked(3800, 4900),
            PLAYER_BLUE1: parked(-3800, -4900, boost=50 + round(angle / 2)),
            PLAYER_BLUE2: parked(-3500, -4900, boost=10 * chain),
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

    # ===== ANGLE DEMANDE (le cas que le banc ne testait PAS) =====
    # Jusqu'ici le banc passait toujours Me.Forward : angle 0, wavedash parfaitement aligne.
    # En jeu, Drive demande un wavedash vers une CIBLE, donc souvent de biais — et c'est la que
    # la voiture derive et perd de la vitesse. Ces scenarios balayent l'angle.
    #
    # Colonnes a lire dans la ligne FIN :
    #   derive= : deviation LATERALE (uu) par rapport a la direction demandee. Signe = cote.
    #             Une derive qui croit vite avec l'angle = la manoeuvre ne tient pas le cap.
    #   capFin= : ecart (deg) entre la direction demandee et la vitesse reelle a l'arrivee.
    #             C'est la mesure directe du "il part de travers".
    #   gain=   : si le gain s'effondre quand l'angle monte, l'angle limite utile est la.
    #
    # OBJECTIF : trouver l'angle au-dela duquel le wavedash ne vaut plus le coup, pour que Drive
    # cesse de le demander (garde d'alignement, actuellement 0.1 rad ~ 6 deg avant declenchement).
    ("N0  angle 0 (reference alignee)",  wd(1200, angle=0)),
    ("N1  angle +10 deg",                wd(1200, angle=10)),
    ("N2  angle +20 deg",                wd(1200, angle=20)),
    ("N3  angle +45 deg",                wd(1200, angle=45)),
    ("N4  angle +90 deg",                wd(1200, angle=90)),
    ("N5  angle -20 deg (symetrie)",     wd(1200, angle=-20)),
    ("N6  angle -45 deg (symetrie)",     wd(1200, angle=-45)),

    # Meme balayage a haute vitesse : le rayon de virage explose, donc un angle qui passe a 1200
    # peut ne plus passer a 2000. C'est le cas reel en sortie de rotation.
    ("N7  angle +20 deg a v0=2000",      wd(2000, angle=20)),
    ("N8  angle +45 deg a v0=2000",      wd(2000, angle=45)),

    # Variante BOOSTEE aux memes angles (mettre WavedashBenchBoost=true) : c'est elle qui tourne
    # en jeu des que boost>30, et c'est elle qui ne stabilisait ni le lacet ni le roulis.
    # Comparer derive= et capFin= avec les N* correspondants.
    ("N9  angle +20 deg (variante boostee)", wd(1200, angle=20)),
    ("N10 angle +45 deg (variante boostee)", wd(1200, angle=45)),

    # ===== ENCHAINEMENT (le 2e wavedash qui degenere) =====
    # Le banc ne faisait qu'UN wavedash : impossible d'y voir le bug d'enchainement. Ces
    # scenarios en lancent plusieurs a la suite, relances des que la voiture est posee avec le
    # MEME delai que Drive (timeOnGround > 0.02s) — donc dans les conditions reelles du jeu.
    #
    # Une ligne DASH n/N par wavedash, puis une ligne FIN CHAINE. Ce qu'on cherche :
    #   - gain= qui s'effondre (ou devient negatif) au 2e/3e dash -> l'enchainement casse ;
    #   - duree= qui gonfle -> le dodge est parti trop tot/tard, ce n'est plus un wavedash ;
    #   - derive= qui explose d'un dash a l'autre -> la voiture part de travers et accumule ;
    #   - capFin= qui derape -> c'est le "front flip qui fait n'importe quoi".
    # Un gain qui decroit DOUCEMENT est normal (on approche du plafond de vitesse) ; c'est la
    # rupture brutale qui signale le bug.
    ("X0  chaine x2 depuis l'arret",    wd(0,    chain=2)),
    ("X1  chaine x3 depuis l'arret",    wd(0,    chain=3)),
    ("X2  chaine x2 a v0=800",          wd(800,  chain=2)),
    ("X3  chaine x3 a v0=800",          wd(800,  chain=3)),
    ("X4  chaine x3 a v0=1400",         wd(1400, chain=3)),
    ("X5  chaine x4 a v0=800 (endurance)", wd(800, chain=4)),

    # Enchainement DE BIAIS : c'est la combinaison qui casse en jeu (rotation = angle + chaine).
    # Comparer a X2/X4 : si seul l'angle degrade la chaine, le plafond a 20 deg est trop haut.
    ("X6  chaine x3 a v0=800, angle +20 deg", wd(800, chain=3, angle=20)),
    ("X7  chaine x3 a v0=800, angle +10 deg", wd(800, chain=3, angle=10)),

    # Sans boost : la variante non boostee est la seule disponible, a comparer avec la boostee
    # (WavedashBenchBoost=true) sur les memes lignes.
    ("X8  chaine x3 a v0=800, boost 0", wd(800, chain=3, boost=0)),

    # ===== DELAI DE RELANCE (le flip precedent qui agit encore) =====
    # Rejouer la pose exacte du dash 2 (position, vitesse, rotation, vitesse ANGULAIRE) ne
    # reproduit PAS l'echec : l'etat fautif n'est donc dans aucun de ces champs. Il vient du
    # flip precedent, qu'aucun champ de Car n'expose et que le state setter remet a zero.
    # Seule facon de le mesurer : faire varier le temps qu'on lui laisse.
    #
    # Lire la ligne RELANCE : depuisDodgePrecedent= donne le temps ecoule depuis l'ENTREE DE
    # DODGE du dash precedent (et non depuis l'atterrissage — c'est le flip qui agit, pas le
    # contact). Chercher le delai a partir duquel le dash 2 retrouve la duree et le gain du
    # dash 1 : c'est le temps que dure reellement l'influence du flip.
    # RESULTAT RETENU : 0.10 s. C'est desormais la valeur de Drive.cs (timeOnGround > 0.1f)
    # et le defaut de wd(). Y0 garde l'ancienne valeur (0.02) comme temoin de l'echec.
    ("Y0  chaine x3, relance 0.02s (ANCIENNE valeur, echec attendu)", wd(800, chain=3, relaunch=0.02)),
    ("Y1  chaine x3, relance 0.08s",                wd(800, chain=3, relaunch=0.08)),
    ("Y2  chaine x3, relance 0.10s (VALEUR RETENUE)", wd(800, chain=3, relaunch=0.10)),
    ("Y3  chaine x3, relance 0.15s",                wd(800, chain=3, relaunch=0.15)),
    ("Y4  chaine x3, relance 0.20s",                wd(800, chain=3, relaunch=0.20)),
    ("Y5  chaine x3, relance 0.30s",                wd(800, chain=3, relaunch=0.30)),
    ("Y6  chaine x3, relance 0.45s",                wd(800, chain=3, relaunch=0.45)),

    # ===== REJEU D'UNE POSE EXACTE (P*) =====
    # Remplacer les valeurs ci-dessous par celles de la ligne [WDBENCH] POSE dash2 relevee
    # en jouant X1. On rejoue alors le dash rate ISOLEMENT et de facon reproductible, ce qui
    # permet de faire varier UN parametre a la fois (angular_velocity, vz, pitch initial...)
    # et de voir lequel casse la manoeuvre.
    #
    # La cle est angular_velocity : c'est la rotation residuelle de l'atterrissage precedent
    # qui fait MONTER le nez au lieu de le piquer, allonge la phase DOWN, et laisse le nez a
    # -23 deg au moment du dodge. Un scenario classique (voiture posee, angvel nulle) ne peut
    # pas reproduire ca — d'ou ce helper.
    # Pose REELLE relevee au depart du dash 2 d'une chaine ([WDBENCH] POSE dash2).
    # La voiture est cap -y (yaw=-1.5734), donc son axe de tangage est ~(-1,0,0) : la vitesse
    # angulaire (-0.509, 0.002, 0.000) est presque entierement un TANGAGE de 0.509 rad/s
    # (~29 deg/s), nez qui REMONTE. Sur les 0.38 s de la phase DOWN cela fait ~11 deg de nez
    # perdu — exactement l'ecart mesure (-1 deg -> +3 deg) qui decale toute la sequence.
    ("P0  pose reelle du dash2 (echec attendu)",
     pose(location=(-3, 2076, 16), rotation=(-0.0213, -1.5734, 0.0000),
          velocity=(-4, -1518, -9), angular_velocity=(-0.509, 0.002, 0.000), boost=94)),

    # --- Variantes de controle : UN seul parametre change a la fois ---

    # Rotation residuelle annulee, tout le reste identique. Si P1 REUSSIT et P0 ECHOUE, la
    # vitesse angulaire est la coupable et c'est ELLE qu'il faut gerer dans Wavedash.
    ("P1  = P0 mais angular_velocity = 0",
     pose(location=(-3, 2076, 16), rotation=(-0.0213, -1.5734, 0.0000),
          velocity=(-4, -1518, -9), angular_velocity=(0.0, 0.0, 0.0), boost=94)),

    # Vitesse verticale annulee, rotation residuelle CONSERVEE. Separe l'effet du rebond
    # (vz=-9) de celui de la rotation. Attendu : echoue comme P0 si c'est bien la rotation.
    ("P2  = P0 mais vz = 0",
     pose(location=(-3, 2076, 16), rotation=(-0.0213, -1.5734, 0.0000),
          velocity=(-4, -1518, 0), angular_velocity=(-0.509, 0.002, 0.000), boost=94)),

    # Balayage de l'intensite : a partir de quelle rotation residuelle la manoeuvre casse ?
    # Donne le SEUIL a partir duquel Wavedash doit compenser (ou refuser de partir).
    ("P3  = P0, rotation a -0.25 (moitie)",
     pose(location=(-3, 2076, 16), rotation=(-0.0213, -1.5734, 0.0000),
          velocity=(-4, -1518, -9), angular_velocity=(-0.25, 0.002, 0.000), boost=94)),
    ("P4  = P0, rotation a -0.75 (une fois et demie)",
     pose(location=(-3, 2076, 16), rotation=(-0.0213, -1.5734, 0.0000),
          velocity=(-4, -1518, -9), angular_velocity=(-0.75, 0.002, 0.000), boost=94)),

    # Rotation INVERSE (nez qui pique au lieu de remonter) : la phase DOWN devrait etre plus
    # rapide que la normale. Si ca casse aussi, c'est toute la sequence qui est trop rigide.
    ("P5  = P0, rotation inverse +0.509",
     pose(location=(-3, 2076, 16), rotation=(-0.0213, -1.5734, 0.0000),
          velocity=(-4, -1518, -9), angular_velocity=(0.509, 0.002, 0.000), boost=94)),
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
