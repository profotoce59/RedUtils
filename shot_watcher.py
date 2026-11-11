"""
Presse-papier des tirs — rejoue un tir rate A L'IDENTIQUE.

LE PROBLEME : quand on VOIT un tir rater, la situation qui l'a produit est deja passee.
La reconstruire de memoire dans un state setter revient a tester un AUTRE scenario que
celui qui a echoue, et donc a corriger contre un fantome.

LE MECANISME : cote C# (Bot/ShotWatcher.cs, flag Fixes.ShotWatcher), la voiture observee
garde en tampon circulaire la pose complete du match (balle + les 4 voitures) et n'en
RETIENT que celles qui precedent de 0.5 s le depart d'un tir — l'instant ou le bot avait
encore le choix. Chaque capture est une ligne de shot_captures.jsonl.

CE PROGRAMME : lit ce fichier et permet, sans rien recompiler,
  - de REJOUER une capture dans le jeu (state setting, comme les scripts de test) ;
  - d'en sortir le scenario Python pret a coller dans un state_setting_tests_*.py ;
  - de le copier dans le presse-papier Windows.

WORKFLOW TYPE
    1. RedUtils/Fixes.cs : ShotWatcher = true (c'est le defaut).
    2. dotnet build Bot.sln
    3. Lancer un match RLBot (state setting actif).
    4. Jouer. Chaque depart de tir imprime une ligne [SHOTWATCH] et remplit le fichier.
    5. Un tir rate ? Ici : "d" rejoue le dernier, ou "<n>" rejoue celui qu'on veut.
       Il se rejoue autant de fois qu'on veut, y compris apres avoir change le code.
    6. Le scenario garde : "c <n>", puis coller dans un state_setting_tests_*.py.

UTILISATION
    python shot_watcher.py            menu interactif
    python shot_watcher.py --list     liste les captures et sort
    python shot_watcher.py --watch    affiche les captures en direct pendant qu'on joue

Les commandes du menu marchent aussi directement en ligne de commande, a l'identique :
    python shot_watcher.py c 74       copie le scenario 74 dans le presse-papier
    python shot_watcher.py p 74       affiche le scenario 74
    python shot_watcher.py 74         rejoue la capture 74 dans le jeu
    python shot_watcher.py d          rejoue la DERNIERE capture

La connexion au jeu n'est etablie qu'au premier REJEU : lire, afficher et copier des
captures fonctionne sans RLBot lance.
"""

import argparse
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
CAPTURE_FILE = os.environ.get("SHOT_WATCHER_FILE", os.path.join(HERE, "shot_captures.jsonl"))

# Le setup manager n'est importe qu'au moment du rejeu : le reste du programme doit
# marcher sans rlbot installe / sans match lance.
_setup_manager = None


# ----------------------------------------------------------------------------- lecture

def load_captures(path=CAPTURE_FILE):
    """Lit le fichier de captures. Une ligne illisible (ecriture coupee net a la
    fermeture du bot) est ignoree plutot que de faire echouer tout le fichier."""
    if not os.path.exists(path):
        return []
    out = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return out


def latest_session(captures):
    return captures[-1].get("session") if captures else None


def filter_session(captures, session):
    if session is None:
        return captures
    return [c for c in captures if c.get("session") == session]


def ascii_only(text):
    """Les intents contiennent une fleche unicode ("Shot->LeurBut" s'ecrit avec ->
    en C#). Les scripts de state setting sont en pur ASCII : on s'y tient, sinon la
    ligne collee peut casser selon l'encodage de l'editeur."""
    return (text or "").replace("→", "->").encode("ascii", "replace").decode("ascii")


def shooter_car(cap):
    for car in cap.get("cars", []):
        if car.get("name") == cap.get("shooter"):
            return car
    return cap.get("cars", [{}])[0] if cap.get("cars") else {}


def speed(vec):
    return (vec[0] ** 2 + vec[1] ** 2 + vec[2] ** 2) ** 0.5


def describe(cap):
    """Une ligne de liste : de quoi RETROUVER le tir qu'on a vu rater, sans relire les logs."""
    me = shooter_car(cap)
    ball = cap["ball"]["location"]
    loc = me.get("location", [0, 0, 0])
    vel = me.get("velocity", [0, 0, 0])
    contact = cap.get("contact_in")
    contact_txt = "contact+%.2fs" % contact if contact is not None else "           "
    return ("t=%7.1f  %-14s %-18s %s  ball=(%5.0f,%6.0f,%4.0f)  moi=(%5.0f,%6.0f) v=%4.0f boost=%3d  [T-%.2fs]"
            % (cap.get("shot_time", 0), cap.get("shot", "?"), ascii_only(cap.get("intent")),
               contact_txt, ball[0], ball[1], ball[2], loc[0], loc[1], speed(vel),
               me.get("boost", 0), cap.get("delay", 0)))


# ------------------------------------------------------------------- scenario Python

def v3(vec, decimals=0):
    fmt = "%." + str(decimals) + "f"
    return "Vector3(%s, %s, %s)" % (fmt % vec[0], fmt % vec[1], fmt % vec[2])


def snippet(cap, index=0):
    """Le scenario Python, exactement au format des state_setting_tests_*.py : un tuple
    (nom, GameState) collable tel quel dans leur liste TEST_STATES."""
    ball = cap["ball"]
    title = "SHOT%d %s %s - t=%.1fs (pose a T-%.2fs)" % (
        index, cap.get("shot", "?"), ascii_only(cap.get("intent")),
        cap.get("shot_time", 0), cap.get("delay", 0))

    lines = []
    lines.append("    # Capture automatique (shot_watcher.py) : pose du match %.2f s AVANT le depart"
                 % cap.get("delay", 0))
    lines.append("    # d'un %s. Rejouee telle quelle, elle remet le bot devant le meme choix." % cap.get("shot", "tir"))
    contact = cap.get("contact_in")
    if contact is not None:
        tgt = cap.get("shot_target")
        lines.append("    # Le tir visait %s, contact prevu %.2f s apres son depart." % (
            ("(%.0f,%.0f,%.0f)" % tuple(tgt)) if tgt else "?", contact))
    lines.append("    (")
    lines.append('        "%s",' % title)
    lines.append("        GameState(")
    lines.append("            ball=BallState(physics=Physics(")
    lines.append("                location=%s," % v3(ball["location"], 0))
    lines.append("                velocity=%s," % v3(ball["velocity"], 0))
    lines.append("                angular_velocity=%s," % v3(ball["angular_velocity"], 3))
    lines.append("            )),")
    lines.append("            cars={")
    for car in cap.get("cars", []):
        tag = "%s, equipe %s" % (ascii_only(car.get("name", "?")),
                                 "bleue" if car.get("team") == 0 else "orange")
        if car.get("name") == cap.get("shooter"):
            tag += " -- LE TIREUR"
        if car.get("demolished"):
            tag += " (demoli au moment de la capture)"
        rot = car["rotation"]
        lines.append("                %d: CarState(  # %s" % (car["index"], tag))
        lines.append("                    physics=Physics(")
        lines.append("                        location=%s," % v3(car["location"], 0))
        lines.append("                        rotation=Rotator(pitch=%.4f, yaw=%.4f, roll=%.4f),"
                     % (rot[0], rot[1], rot[2]))
        lines.append("                        velocity=%s," % v3(car["velocity"], 0))
        lines.append("                        angular_velocity=%s," % v3(car["angular_velocity"], 3))
        lines.append("                    ),")
        lines.append("                    boost_amount=%d," % car.get("boost", 0))
        lines.append("                ),")
    lines.append("            },")
    lines.append("        ),")
    lines.append("    ),")
    return "\n".join(lines)


def copy_to_clipboard(text):
    """clip.exe attend de l'UTF-16LE ; lui passer de l'UTF-8 mange les accents."""
    try:
        proc = subprocess.Popen("clip", stdin=subprocess.PIPE, shell=True)
        proc.communicate(text.encode("utf-16-le"))
        return proc.returncode == 0
    except OSError:
        return False


# ------------------------------------------------------------------------- rejeu jeu

def game_state_from(cap):
    from rlbot.utils.game_state_util import (
        GameState, BallState, CarState, Physics, Vector3, Rotator,
    )

    ball = cap["ball"]
    cars = {}
    for car in cap.get("cars", []):
        rot = car["rotation"]
        cars[car["index"]] = CarState(
            physics=Physics(
                location=Vector3(*car["location"]),
                rotation=Rotator(pitch=rot[0], yaw=rot[1], roll=rot[2]),
                velocity=Vector3(*car["velocity"]),
                angular_velocity=Vector3(*car["angular_velocity"]),
            ),
            boost_amount=car.get("boost", 0),
        )
    return GameState(
        ball=BallState(physics=Physics(
            location=Vector3(*ball["location"]),
            velocity=Vector3(*ball["velocity"]),
            angular_velocity=Vector3(*ball["angular_velocity"]),
        )),
        cars=cars,
    )


def ensure_setup():
    """Connexion differee : lire/afficher/copier doit marcher sans match lance."""
    global _setup_manager
    if _setup_manager is None:
        from rlbot.setup_manager import SetupManager
        sm = SetupManager()
        sm.connect_to_game()
        _setup_manager = sm
        print("Connecte au jeu.")
    return _setup_manager


def replay(cap):
    sm = ensure_setup()
    sm.game_interface.set_game_state(game_state_from(cap))
    print("-> rejoue : %s\n" % describe(cap))


# ----------------------------------------------------------------------------- modes

def watch(path=CAPTURE_FILE):
    """Suit le fichier en direct : chaque tir declenche est affiche des qu'il tombe."""
    print("Surveillance de %s (Ctrl+C pour arreter).\n" % path)
    seen = len(load_captures(path))
    while True:
        caps = load_captures(path)
        for i in range(seen, len(caps)):
            print("[%3d] %s" % (i, describe(caps[i])))
        seen = len(caps)
        time.sleep(0.5)


def show_list(caps, session_label):
    if not caps:
        print("Aucune capture. Verifie que Fixes.ShotWatcher = true, que le bot s'appelle")
        print("MyBot (voir MyBot.ShotWatcherCarName) et qu'un tir a bien ete declenche.\n")
        return
    print("\n%d captures (%s) :" % (len(caps), session_label))
    for i, cap in enumerate(caps):
        print("  [%3d] %s" % (i, describe(cap)))
    print("")


def pick(caps, arg):
    try:
        return caps[int(arg)]
    except (ValueError, IndexError):
        print("Numero invalide.\n")
        return None


def emit_snippet(caps, index, to_clipboard):
    """Affiche ou copie le scenario d'une capture. Partage entre le menu et la ligne de
    commande, pour que 'c 74' fasse exactement la meme chose dans les deux."""
    cap = pick(caps, index)
    if cap is None:
        return
    text = snippet(cap, int(index))
    if not to_clipboard:
        print("\n" + text + "\n")
    elif copy_to_clipboard(text):
        print("Scenario copie dans le presse-papier — colle-le dans un state_setting_tests_*.py\n")
    else:
        print("Copie impossible, voici le scenario :\n\n" + text + "\n")


def run_command(cmd):
    """Execute une commande du menu (liste de tokens). Rend True si elle a ete reconnue.
    Utilise aussi bien depuis le prompt interactif que depuis les arguments CLI."""
    allc = load_captures()
    caps = filter_session(allc, latest_session(allc))
    head = cmd[0].lower()

    if head in ("p", "c") and len(cmd) > 1:
        emit_snippet(caps, cmd[1], to_clipboard=(head == "c"))
        return True

    if head == "d":
        if caps:
            replay(caps[-1])
        else:
            print("Aucune capture a rejouer.\n")
        return True

    try:
        int(head)
    except ValueError:
        return False

    cap = pick(caps, head)
    if cap is not None:
        replay(cap)
    return True


def menu():
    all_session = False
    caps_all = load_captures()
    session = latest_session(caps_all)
    caps = filter_session(caps_all, session)

    print(__doc__.split("WORKFLOW TYPE")[0].strip())
    print("\nFichier : %s" % CAPTURE_FILE)

    while True:
        label = "toutes sessions" if all_session else "session %s" % session
        show_list(caps, label)
        print("  <n>    rejouer la capture n dans le jeu       p <n>  afficher le scenario Python")
        print("  d      rejouer la DERNIERE capture            c <n>  copier le scenario (presse-papier)")
        print("  r      relire le fichier                      a      basculer toutes sessions / derniere")
        print("  x      vider le fichier de captures           Entree quitter")
        choice = input("\n> ").strip()

        if choice == "":
            break

        cmd = choice.split()
        head = cmd[0].lower()

        if head == "r":
            caps_all = load_captures()
            session = latest_session(caps_all)
            caps = caps_all if all_session else filter_session(caps_all, session)
            continue

        if head == "a":
            all_session = not all_session
            caps = caps_all if all_session else filter_session(caps_all, session)
            continue

        if head == "x":
            if input("Vider %s ? (o/N) " % os.path.basename(CAPTURE_FILE)).strip().lower() == "o":
                open(CAPTURE_FILE, "w").close()
                caps_all, caps, session = [], [], None
                print("Fichier vide.\n")
            continue

        if head in ("p", "c") and len(cmd) > 1:
            emit_snippet(caps, cmd[1], to_clipboard=(head == "c"))
            continue

        if head == "d":
            if caps:
                replay(caps[-1])
            continue

        cap = pick(caps, head)
        if cap is not None:
            replay(cap)


def main():
    parser = argparse.ArgumentParser(
        description="Rejoue les poses capturees juste avant un tir.",
        epilog="Sans argument : menu interactif. Avec une commande du menu "
               "(c 74, p 74, 74, d) : execute puis sort.")
    parser.add_argument("--watch", action="store_true", help="affiche les captures en direct")
    parser.add_argument("--list", action="store_true", help="liste les captures et sort")
    parser.add_argument("command", nargs="*",
                        help="commande du menu a executer directement (ex: c 74, p 74, 74, d)")
    args = parser.parse_args()

    if args.watch:
        try:
            watch()
        except KeyboardInterrupt:
            print("")
        return

    if args.list:
        caps = load_captures()
        show_list(filter_session(caps, latest_session(caps)), "derniere session")
        return

    if args.command:
        if not run_command(args.command):
            print("Commande inconnue : %s" % " ".join(args.command))
            print("Attendu : un numero, 'd', 'p <n>' ou 'c <n>'. Sans argument = menu interactif.")
        return

    menu()


if __name__ == "__main__":
    main()
