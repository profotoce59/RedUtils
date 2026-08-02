"""
Banc de mesure de la ROTATION — garde-t-on la vitesse en se replacant ?

Le bot ne joue PAS la balle. Il execute juste une Rotate depuis la pose imposee ici :
sortie tout droit -> gros pad du cote oppose (s'il s'aborde sous 45 deg) -> arc de
cercle vers le replacement. On mesure le PROFIL DE VITESSE du trajet.

Workflow :
    1. RedUtils/Fixes.cs : RotationBench = true
    2. dotnet build Bot.sln
    3. Lancer un match RLBot (state setting actif), 4 voitures.
    4. python state_setting_tests_rotation.py, puis derouler les scenarios un par un.

Sortie :

    [ROTBENCH] DEPART v0=1600 boost0=12 pos=(2000,1500) cote=-x dest=(-640,-4780) pad=(-3584,0)
    [ROTBENCH] FIN duree=3.42s v0=1600 vMin=1180 vMoy=1750 vMax=2300 vFin=2280
               tempsLent=0.00s (0%) boostPris=+100 boostFin=88 dist=5400 padVise=oui resteAFaire=180

LECTURE — la colonne qui juge la rotation est vMin, pas vMoy :
  - vMin        -> vitesse la plus BASSE du trajet. C'est LA metrique : une rotation
                   reussie ne casse jamais la vitesse. Si vMin s'effondre, l'arc est
                   trop serre -> baisser ArcMaxAngle dans Rotate.cs.
  - tempsLent   -> temps passe sous 1000 uu/s. Doit rester a ~0.
  - boostPris   -> +100 si le gros pad a bien ete pris ; +0 avec padVise=oui signifie
                   qu'on l'a manque (angle d'entree ou trajectoire a revoir).
  - padVise=non -> aucun gros pad du cote oppose ne convenait. La RAISON de chaque refus
                   est imprimee juste avant la ligne DEPART :
                       [Rotate] pad (-3584,0) ecarte : virage au pad 128 deg > 80 deg
                       [Rotate] pad (-3072,4096) ecarte : detour 4580 > 4000
                   Reglages correspondants dans Rotate.cs : PadTurnMaxAngle, PadMaxDetour.
  - resteAFaire -> distance restante a la destination. Grand + TIMEOUT = la rotation
                   n'a pas converge.

REGLAGE : faire varier ArcMaxAngle (Rotate.cs) et rejouer les memes scenarios. On cherche
la plus grande valeur qui garde vMin haut — plus l'arc est large, plus le retour est long.

Nous = PLAYER_ORANGE1 (index 2), equipe orange (notre but en +y). Les 4 voitures tournent
le meme bot mais seule l'index 2 mesure et imprime (filtre RotationBenchCarIndex cote C#).

Chaque scenario simule « je viens d'engager un contest et je repars » : voiture lancee a
v0, orientee dans la direction ou l'engagement l'a laissee. La balle est placee pour fixer
la destination (DefensivePosition en depend), mais elle n'est jamais jouee.
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
PLAYER_ORANGE1 = 2   # nous — c'est cette voiture qui mesure
PLAYER_ORANGE2 = 3   # coequipier, gare hors du chemin

ONGROUNDHEIGHT = 17
BALLGROUND = 93

# Orientations (radians). Notre but est en +y (equipe orange).
YAW_VERS_LEUR_BUT = 4.71    # -y : on fonce vers le but adverse
YAW_VERS_NOTRE_BUT = 1.57   # +y : on rentre
YAW_VERS_PLUS_X = 0.0
YAW_VERS_MOINS_X = 3.14


def parked(x, y, boost=0):
    return CarState(
        physics=Physics(
            location=Vector3(x, y, ONGROUNDHEIGHT),
            rotation=Rotator(pitch=0, yaw=YAW_VERS_LEUR_BUT, roll=0),
            velocity=Vector3(0, 0, 0),
            angular_velocity=Vector3(0, 0, 0),
        ),
        boost_amount=boost,
    )


def rot(x, y, yaw, v0, boost=0, ball=(0.0, -1500.0)):
    """Voiture lancee a v0 dans la direction yaw, depuis (x, y).

    ball fixe la destination du replacement (DefensivePosition en depend) ; elle n'est
    jamais jouee. Le coequipier est gare loin : il ne gene pas la trajectoire.

    Par defaut la balle est AU CENTRE, et c'est VOULU : la destination tombe alors en
    x=0, donc le pad du cote oppose devient un vrai zigzag (detour mesure 4437 contre
    3823 avec une balle sur le cote). C'est le cas DEFAVORABLE, donc celui qui doit
    servir de reference — si la logique n'y survit pas, c'est la logique qu'il faut
    corriger, pas le scenario.

    Les scenarios F* posent au contraire la balle la ou l'on vient de contester (sur la
    voiture) : DefensivePosition decale alors le poste au poteau OPPOSE a la balle, donc
    du meme cote que les pads de rotation. A comparer paire a paire avec A0/D0.
    """
    import math
    vx = math.cos(yaw) * v0
    vy = math.sin(yaw) * v0
    return GameState(
        ball=BallState(physics=Physics(
            location=Vector3(ball[0], ball[1], BALLGROUND),
            velocity=Vector3(-10, -10, 0),
            angular_velocity=Vector3(0, 0, 0),
        )),
        cars={
            PLAYER_ORANGE1: CarState(
                physics=Physics(
                    location=Vector3(x, y, ONGROUNDHEIGHT),
                    rotation=Rotator(pitch=0, yaw=yaw, roll=0),
                    velocity=Vector3(vx, vy, 0),
                    angular_velocity=Vector3(0, 0, 0),
                ),
                boost_amount=boost,
            ),
            PLAYER_ORANGE2: parked(3800, 4900),
            PLAYER_BLUE1: parked(-3800, -4900),
            PLAYER_BLUE2: parked(-3500, -4900),
        },
    )


TEST_STATES = [
    # ===== CAS NOMINAL : contest dans leur moitie, on repart =====
    # On vient de toucher la balle cote +x et on file encore vers -y (leur but).
    # La rotation doit partir tout droit, prendre un pad cote -x, puis arquer vers notre but.
    ("A0  contest cote +x, lance a 1400, 0 boost", rot(2000, -1500, YAW_VERS_LEUR_BUT, 1400)),
    ("A1  contest cote +x, lance a 2300, 0 boost", rot(2000, -1500, YAW_VERS_LEUR_BUT, 2300)),
    ("A2  contest cote +x, lance a 1400, 30 boost", rot(2000, -1500, YAW_VERS_LEUR_BUT, 1400, boost=30)),
    ("A3  contest cote -x (miroir de A0)", rot(-2000, -1500, YAW_VERS_LEUR_BUT, 1400)),

    # ===== CHOIX DU PAD =====
    # Le cap de depart ne doit PAS decider du pad (c'est l'arc qui absorbe le cap) : ces 4
    # scenarios partent du meme point avec des caps opposes et devraient retenir le MEME pad.
    # Si padVise change avec le cap, le filtre regarde encore le mauvais vecteur.
    ("B0  cap vers leur but", rot(2500, 0, YAW_VERS_LEUR_BUT, 1600)),
    ("B1  cap plein -x", rot(2500, 0, YAW_VERS_MOINS_X, 1600)),
    ("B2  cap plein +x", rot(2500, 0, YAW_VERS_PLUS_X, 1600)),
    ("B3  cap deja vers notre but", rot(2500, 0, YAW_VERS_NOTRE_BUT, 1600)),

    # ===== SERRAGE DE L'ARC (reglage d'ArcMaxAngle) =====
    # Depart tres desaligne : c'est le cas qui casse la vitesse si l'arc est trop serre.
    # Regarder vMin. Rejouer apres avoir change ArcMaxAngle dans Rotate.cs.
    ("C0  desaligne 90 deg, lance a 2000", rot(3000, -2000, YAW_VERS_PLUS_X, 2000)),
    ("C1  desaligne 180 deg (dos a la destination)", rot(0, 3000, YAW_VERS_LEUR_BUT, 2000)),
    ("C2  desaligne 90 deg a vitesse moyenne", rot(3000, -2000, YAW_VERS_PLUS_X, 1200)),

    # ===== FOND DE TERRAIN ADVERSE (longue rotation) =====
    # Le trajet le plus long : c'est la que garder la vitesse rapporte le plus.
    ("D0  fond adverse, cote +x, 2300", rot(2500, -4000, YAW_VERS_LEUR_BUT, 2300)),
    ("D1  fond adverse, cote +x, 800", rot(2500, -4000, YAW_VERS_LEUR_BUT, 800)),

    # ===== VARIANTE : BALLE LA OU L'ON A CONTESTE (cas favorable) =====
    # Tous les scenarios ci-dessus ont la balle AU CENTRE (cas defavorable, reference).
    # Ici la balle est sur la voiture : DefensivePosition decale le poste au poteau oppose
    # a la balle, donc du meme cote que les pads -> le detour du pad du milieu tombe de
    # 4437 a 3823. A comparer PAIRE A PAIRE avec A0 et D0 (memes poses, balle differente) :
    # l'ecart mesure ce que la position de la balle change au choix du pad.
    ("F0  = A0 mais balle sur le contest", rot(2000, -1500, YAW_VERS_LEUR_BUT, 1400, ball=(2000.0, -1500.0))),
    ("F1  = D0 mais balle sur le contest", rot(2500, -4000, YAW_VERS_LEUR_BUT, 2300, ball=(2500.0, -4000.0))),
    ("F2  pose du log reel, balle sur le contest", rot(1996, -3025, YAW_VERS_LEUR_BUT, 1400, ball=(1996.0, -3025.0))),

    # ===== DEPART LENT (sortie de 50/50) =====
    # Apres un contact on est souvent lent : verifier que la rotation relance
    # (speedflip si boost) au lieu de rester poussive. Comparer vMax a v0.
    ("E0  sortie de 50/50, v0=300, 0 boost", rot(1500, -800, YAW_VERS_LEUR_BUT, 300)),
    ("E1  sortie de 50/50, v0=300, 40 boost", rot(1500, -800, YAW_VERS_LEUR_BUT, 300, boost=40)),
]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} scenarios de mesure de la rotation.\n")
    print("Rappel : Fixes.RotationBench doit etre a true (le bot ignore alors sa strategie).")
    print("Laisse chaque rotation FINIR (ligne FIN/TIMEOUT) avant de lancer la suivante.")
    print("La colonne qui juge : vMin. Si elle s'effondre, baisser ArcMaxAngle (Rotate.cs).\n")

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
