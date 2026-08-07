"""
State setters de test pour l'ATTAQUE / le PRESSING (Fixes.OffensivePressing).

Constat : en attaque le bot est trop passif. Ces scenarios isolent les deux cas
qui doivent le rendre agressif :
    A) ON a la balle dans LEUR camp  -> il doit finir (tir / carry vers leur but).
    B) EUX ont la balle dans LEUR camp -> il doit PRESSER (contester haut), pas
       redescendre au rond central en attendant.

Workflow :
    1. Verifie Fixes.OffensivePressing dans RedUtils/Fixes.cs
       (true = pressing offensif, false = comportement d'origine = shadow/repli).
    2. Recompile : dotnet build Bot.sln
    3. Relance le match RLBot (state setting active).
    4. Lance ce script, applique un scenario, observe l'overlay INTENT + les logs.

Quoi observer (overlay INTENT + logs console) :
    AGRESSIF (ce qu'on veut en attaque) :
    - "Shot→LeurBut"   : le bot tente un tir / une reprise vers leur but
    - "Drive→Pressing" : il monte contester la balle dans leur camp
    - "Dribble" / "Drive→Contour" : il porte la balle vers leur but
    PASSIF (le probleme a traquer) :
    - "Drive→Shadow"      : il se replie a 60% vers notre but (= rond central ici)
    - "Arrive→BackupPos"  : il reste en soutien au lieu de finir
    - "GetBoost"          : il part chercher du boost alors qu'il devrait presser
    - "Drive→Balle" qui traine sans jamais declencher de Shot→LeurBut

Nous = PLAYER_ORANGE1 (index 2), notre but en y = +5120.
On attaque vers -y ; LEUR but (bleu) est en y = -5120. Leur camp = y < 0.
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
YAW_ORANGE = 1.5708 # +y (vers le but orange = notre but) — cap d'attaque des BLEUS
YAW_LEFT = 3.14     # -x
YAW_BLUE = 4.71     # -y (vers le but bleu = leur but) — notre cap d'attaque


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
    # ==================================================================
    # A) ON A LA BALLE DANS LEUR CAMP — le bot doit FINIR, pas temporiser
    # ==================================================================

    # ------------------------------------------------------------------
    # ATT1 — Possession, balle lente qui roule vers leur but.
    # Adversaires loin derriere. ATTENDU : Shot→LeurBut (ou carry puis tir).
    # PASSIF a signaler : Drive→Balle qui traine, ou repli.
    # ------------------------------------------------------------------
    (
        "ATT1 - Possession balle lente vers leur but",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, -2800, 93),
                velocity=Vector3(0, -300, 0),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, -1400, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, -400, 0),
                    ),
                    boost_amount=60,
                ),
                PLAYER_ORANGE2: parked(2200, 2600),
                PLAYER_BLUE1: parked(0, -4800),
                PLAYER_BLUE2: parked(1500, -4600),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # ATT2 — Balle qui file vite vers leur but, on suit juste derriere.
    # But grand ouvert. ATTENDU : on rattrape et Shot→LeurBut.
    # PASSIF a signaler : le bot leve le pied / laisse filer.
    # ------------------------------------------------------------------
    (
        "ATT2 - Finition, balle file vers but ouvert",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(-600, -3200, 93),
                velocity=Vector3(0, -800, 0),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(-600, -1800, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, -900, 0),
                    ),
                    boost_amount=40,
                ),
                PLAYER_ORANGE2: parked(-2200, 2600),
                PLAYER_BLUE1: parked(2000, -4600),
                PLAYER_BLUE2: parked(-1200, -4800),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # ATT3 — Rebond haut dans leur surface (reprise de volee / aerial).
    # Boost genereux. ATTENDU : JumpShot/DoubleJump/Aerial vers le but.
    # PASSIF a signaler : le bot attend que ca retombe au sol.
    # ------------------------------------------------------------------
    (
        "ATT3 - Reprise sur rebond haut dans leur surface",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(300, -3600, 700),
                velocity=Vector3(0, -150, -60),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(300, -2200, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=80,
                ),
                PLAYER_ORANGE2: parked(-2200, 2600),
                PLAYER_BLUE1: parked(-1500, -4700),
                PLAYER_BLUE2: parked(1500, -4700),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # ATT4 — Balle lente au poteau, angle ouvert sur le but.
    # ATTENDU : Shot→LeurBut dans le filet ouvert.
    # PASSIF a signaler : Drive→Balle qui tourne autour sans tirer.
    # ------------------------------------------------------------------
    (
        "ATT4 - Angle ouvert au poteau, doit tirer",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(2200, -3800, 93),
                velocity=Vector3(0, -120, 0),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(1300, -2500, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, -300, 0),
                    ),
                    boost_amount=50,
                ),
                PLAYER_ORANGE2: parked(-2200, 2600),
                PLAYER_BLUE1: parked(-500, -4900),
                PLAYER_BLUE2: parked(-2000, -4600),
            },
        ),
    ),

    # ==================================================================
    # B) EUX ONT LA BALLE DANS LEUR CAMP — le bot doit PRESSER haut
    # ==================================================================

    # ------------------------------------------------------------------
    # ATT5 — Un adversaire vient de recevoir la balle dans son camp.
    # On a du boost, coequipier en couverture derriere.
    # ATTENDU : Drive→Pressing (on monte contester haut).
    # PASSIF a signaler : Drive→Shadow / repli au rond central.
    # ------------------------------------------------------------------
    (
        "ATT5 - Adversaire porte la balle, presser haut",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(500, -3300, 120),
                velocity=Vector3(0, 100, 0),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(200, -900, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=70,
                ),
                PLAYER_ORANGE2: parked(1000, 2200),
                PLAYER_BLUE1: CarState(
                    physics=Physics(
                        location=Vector3(500, -3150, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_ORANGE, roll=0),
                        velocity=Vector3(0, 150, 0),
                    ),
                    boost_amount=50,
                ),
                PLAYER_BLUE2: parked(-500, -4600),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # ATT6 — Balle libre dans leur camp, on est plus proche que l'adversaire.
    # ATTENDU : Drive→Pressing / on va la gagner (puis Shot→LeurBut).
    # PASSIF a signaler : Drive→Shadow alors qu'on avait la course.
    # ------------------------------------------------------------------
    (
        "ATT6 - Balle libre leur camp, course a la possession",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(0, -2500, 93),
                velocity=Vector3(0, 0, -5),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(0, -900, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=50,
                ),
                PLAYER_ORANGE2: parked(2000, 2200),
                PLAYER_BLUE1: parked(0, -4200),
                PLAYER_BLUE2: parked(-1800, -4600),
            },
        ),
    ),

    # ------------------------------------------------------------------
    # ATT7 — Degagement adverse FAIBLE : la balle revient mollement vers
    # le milieu de leur camp. ATTENDU : contre-pression immediate, on
    # remonte la balle (Drive→Pressing puis Shot/Dribble).
    # PASSIF a signaler : le bot recule pour shadow au lieu de re-attaquer.
    # ------------------------------------------------------------------
    (
        "ATT7 - Degagement faible, contre-pression",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(-300, -2400, 350),
                velocity=Vector3(150, 450, -50),
                angular_velocity=Vector3(0, 0, 0),
            )),
            cars={
                PLAYER_ORANGE1: CarState(
                    physics=Physics(
                        location=Vector3(-300, -500, ONGROUNDHEIGHT),
                        rotation=Rotator(pitch=0, yaw=YAW_BLUE, roll=0),
                        velocity=Vector3(0, 0, 0),
                    ),
                    boost_amount=60,
                ),
                PLAYER_ORANGE2: parked(1800, 2200),
                PLAYER_BLUE1: parked(-300, -3600),
                PLAYER_BLUE2: parked(1500, -4600),
            },
        ),
    ),
        # Capture automatique (shot_watcher.py) : pose du match 0.52 s AVANT le depart
    # d'un GroundShot. Rejouee telle quelle, elle remet le bot devant le meme choix.
    # Le tir visait (-790,-5213,93), contact prevu 5.67 s apres son depart.
        # Capture automatique (shot_watcher.py) : pose du match 0.50 s AVANT le depart
    # d'un GroundShot. Rejouee telle quelle, elle remet le bot devant le meme choix.
    # Le tir visait (716,-5213,93), contact prevu 3.78 s apres son depart.
        # Capture automatique (shot_watcher.py) : pose du match 0.50 s AVANT le depart
    # d'un AerialShot. Rejouee telle quelle, elle remet le bot devant le meme choix.
    # Le tir visait (507,-5213,379), contact prevu 1.63 s apres son depart.
    (
        "SHOT72 AerialShot Shot->LeurBut - t=254.4s (pose a T-0.50s)",
        GameState(
            ball=BallState(physics=Physics(
                location=Vector3(-2779, -4737, 998),
                velocity=Vector3(1720, -554, -298),
                angular_velocity=Vector3(-0.239, -0.296, -0.783),
            )),
            cars={
                0: CarState(  # MyBot-protosv1.2.2, equipe bleue
                    physics=Physics(
                        location=Vector3(-3019, -4983, 679),
                        rotation=Rotator(pitch=0.7342, yaw=-0.5398, roll=1.5777),
                        velocity=Vector3(634, -384, 704),
                        angular_velocity=Vector3(1.496, 1.810, 1.307),
                    ),
                    boost_amount=12,
                ),
                1: CarState(  # MyBot-protosv1.2.2 (2), equipe bleue
                    physics=Physics(
                        location=Vector3(692, -4714, 17),
                        rotation=Rotator(pitch=-0.0096, yaw=2.9945, roll=0.0000),
                        velocity=Vector3(-20, -2, 0),
                        angular_velocity=Vector3(-0.001, 0.001, 0.147),
                    ),
                    boost_amount=81,
                ),
                2: CarState(  # MyBot, equipe orange -- LE TIREUR
                    physics=Physics(
                        location=Vector3(-1550, -1858, 17),
                        rotation=Rotator(pitch=-0.0099, yaw=-1.6510, roll=0.0002),
                        velocity=Vector3(-70, -1832, 0),
                        angular_velocity=Vector3(0.001, -0.001, -1.561),
                    ),
                    boost_amount=62,
                ),
                3: CarState(  # MyBot (2), equipe orange
                    physics=Physics(
                        location=Vector3(-3036, -4838, 992),
                        rotation=Rotator(pitch=-0.1120, yaw=0.5178, roll=-2.6785),
                        velocity=Vector3(1030, -957, 13),
                        angular_velocity=Vector3(-4.170, 0.290, 2.122),
                    ),
                    boost_amount=2,
                ),
            },
        ),
    ),
]


def main():
    sm = SetupManager()
    sm.connect_to_game()

    print(f"Connecte. {len(TEST_STATES)} etats de test charges.\n")
    print("Rappel : compare Fixes.OffensivePressing = true vs false")
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
