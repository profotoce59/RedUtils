# Arbre de décision — RedBot

## Légende
- `→` : action à effectuer
- `| fallback` : si l'action principale échoue (ex: FindShot ne trouve rien), utiliser cette action à la place
- Indentation : niveau de condition imbriquée

## Actions disponibles
| Nom | Description |
|-----|-------------|
| `Drive(Balle)` | Conduite vers la balle |
| `Arrive(BackupPosition)` | Arrivée face à la balle sur la position de soutien (2500u goal-side de la balle, décalée back post) |
| `Arrive(DefensivePosition)` | Arrivée face à la balle sur le point à 20% entre notre but et la balle (couverture dernier homme) |
| `Drive(ShadowPosition)` | Conduite vers le point à 60% entre notre but et la balle — le bot fait face à la balle |
| `FindShot(LeurBut)` | Cherche un tir vers le but adverse |
| `FindShot(Dégagement)` | Cherche un tir pour dégager loin de notre but |
| `GetBoost` | Va chercher le meilleur gros boost disponible |
| `Kickoff` | Speedflip vers la balle au kickoff |
| `Dodge(Balle)` | Dodge en direction de la balle (50/50) |

---

## Calcul de l'état de possession

ETA = temps avant que le joueur puisse intercepter un slice de `Ball.Prediction`.
`ourEta` = min sur notre équipe, `theirEta` = min sur l'équipe adverse **− 0.15s**
_(les adversaires flippent dans la balle, `Drive.GetEta` ne le modélise pas → on est pessimiste)_.

| État | Condition |
|------|-----------|
| `NOT POSSESSED` | `ourEta − theirEta > 0.4s` |
| `POSSESSED` | `ourEta − theirEta < −0.4s` **ET** `theirEta > 0.9s` **ET** pas de balle-projectile |
| `CONTESTED` | tout le reste |

**Fenêtre de contestation (0.9s)** : arriver 0.4s avant un adversaire qui est sur la balle
dans 0.5s, ce n'est pas de la possession — c'est un 50/50.

**Balle-projectile** : si l'adversaire a touché en dernier ET `|Ball.Velocity| > 800 u/s`,
son ETA explose (la balle le fuit) et ça se lit à tort comme de la possession pour nous.
Or personne ne contrôle cette balle → on force `CONTESTED` pour aller la challenger.

**Hystérésis (0.25s)** : un nouvel état doit tenir 0.25s avant d'être adopté.
L'estimateur d'ETA est une fonction en escalier (premier slice atteignable) : sans ça,
une seule frame bruitée bascule toute la stratégie. L'état brut est loggé (`raw=`)
quand il diffère de l'état retenu.

---

## Kickoff
- Coéquipier plus proche de la balle  →  `GetBoost`
- Je suis le plus proche              →  `Kickoff` (non-interruptible)

---

## Hors kickoff

### Attribution des rôles (`ComputeRole`)
Score = ETA vers la balle + pénalité de 2s si l'angle car→balle→leur but dépasse 108°. Score le plus bas = Attacker.
- **Départage d'égalité** : à score strictement égal (kickoff symétrique), l'index le plus bas est Attacker — sinon les deux bots se croient Attacker.
- **Hystérésis (0.3s)** : le titulaire garde son rôle tant que l'autre ne le bat pas de 0.3s. Les deux conditions sont complémentaires, donc les deux bots restent d'accord sans état partagé.

### SUPPORT _(coéquipier présent et mon score de rôle > celui du coéquipier)_
- État `NOT POSSESSED` _(ils ont la balle)_ → `Arrive(DefensivePosition)` face à la balle — dernier homme, il couvre le but **boost ou pas**
- Collecte de boost _(hystérésis : entre si boost < 30, sort à ≥ 60)_ → `GetBoost` limité aux **gros pads goal-side de la balle** ; s'il n'y en a aucun, on se replace sans boost plutôt que de traverser le terrain
- Sinon → `Arrive(BackupPosition)` face à la balle

`BackupPosition` = 2500u goal-side de la **balle** (pas de l'attaquant, qui transmettrait ses erreurs de placement), décalée de 800u vers le poteau **opposé** à la balle (back post — deux bots jamais sur la même ligne), bornée au terrain (marge 400u).

**Latch des actions** : `Drive`/`Arrive`/`GetBoost` ne sont **pas** recréés à chaque tick (la cible est mutée si elle dérive de < 800u). Recréer un `Drive` remet son `timeOnGround` à zéro, ce qui interdit dodges/speedflips/wavedashes (`Drive.cs:160` exige 0.2s au sol) — c'était la cause des replacements lents.

---

### ATTACKER _(coéquipier présent et mon score de rôle ≤ celui du coéquipier)_ / SOLO _(pas de coéquipier)_

#### NOT POSSESSED _(ils ont la balle)_
- Temps avant que la balle passe à < 400u de moi < 1.5s → `Fifty` _(RedUtils/Actions/Fifty.cs)_
  - Approche : Drive vers intercept prédit
  - Contact (ballEta < 0.4s) :
    - z < 250u → Dodge plat
    - z < 400u → Saut + Dodge
    - z ≥ 400u → Saut + Boost + Dodge (aérien)
- Sinon → `Drive(ShadowPosition)` _(60% entre notre but et la balle)_

#### CONTESTED _(ETAs proches, ou balle-projectile adverse, ou adversaire contestable < 0.9s)_
- Zone Offensive _(balle dans leur moitié)_  →  `FindShot(LeurBut)` | `Drive(Balle)`
- Zone Défensive _(balle dans notre moitié)_
  - Temps avant que la balle passe à < 400u de moi < 1.5s → `Fifty` _(classe RedUtils/Actions/Fifty.cs)_
  - Sinon → `Drive(Balle)`
  - Sinon  →  `Drive(Balle)`

#### POSSESSED _(on a la balle, et personne ne peut nous la contester avant 0.9s)_
- Zone Offensive _(balle dans leur moitié)_  →  `FindShot(LeurBut)` | `Drive(Balle)`
- Zone Défensive _(balle dans notre moitié)_
  - Angle face à leur but > 0.15 → `FindShot(LeurBut)`
  - Côté but _(dot > 0.5)_ ET distance < 400u → `Dribble` _(RedUtils/Actions/Dribble.cs)_
    - Balle haute (z > 200u) : se placer sous le point d'atterrissage prédit, vitesse = vitesse horizontale balle + 100
    - Balle basse (z ≤ 200u) : pousser à 1100 u/s vers leur but
  - Sinon → vitesse de la balle vers moi _(Ball.Velocity · ballToMe)_ :
    - `> 500 u/s` _(balle qui arrive vite)_ → `FindShot(Dégagement)` | `Drive→Contour` _(300u côté notre but)_
    - `≤ 500 u/s` _(balle lente ou fuyante)_ :
      - distance < 800u → `Dribble`
      - distance ≥ 800u → `Drive→Contour` _(point prédit : slice où le bot peut atteindre position goal-side à temps)_
