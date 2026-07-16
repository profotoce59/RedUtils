# Arbre de décision — RedBot

## Légende
- `→` : action à effectuer
- `| fallback` : si l'action principale échoue (ex: FindShot ne trouve rien), utiliser cette action à la place
- Indentation : niveau de condition imbriquée

## Actions disponibles
| Nom | Description |
|-----|-------------|
| `Drive(Balle)` | Conduite vers la balle |
| `Drive(BackupPosition)` | Conduite vers la position de soutien (1500u derrière l'attaquant, côté notre but) |
| `Drive(ShadowPosition)` | Conduite vers le point à 60% entre notre but et la balle — le bot fait face à la balle |
| `FindShot(LeurBut)` | Cherche un tir vers le but adverse |
| `FindShot(Dégagement)` | Cherche un tir pour dégager loin de notre but |
| `GetBoost` | Va chercher le meilleur gros boost disponible |
| `Kickoff` | Speedflip vers la balle au kickoff |
| `Dodge(Balle)` | Dodge en direction de la balle (50/50) |

---

## Kickoff
- Coéquipier plus proche de la balle  →  `GetBoost`
- Je suis le plus proche              →  `Kickoff` (non-interruptible)

---

## Hors kickoff

### SUPPORT _(coéquipier présent et mon score de rôle > celui du coéquipier)_
- Boost < 70  →  `GetBoost`
- Boost ≥ 70  →  `Drive(BackupPosition)`

---

### ATTACKER _(coéquipier présent et mon score de rôle ≤ celui du coéquipier)_ / SOLO _(pas de coéquipier)_

#### NOT POSSESSED _(leur ETA < mon ETA − 0.3s — ils ont la balle)_
- Temps avant que la balle passe à < 400u de moi < 1.5s → `Fifty` _(RedUtils/Actions/Fifty.cs)_
  - Approche : Drive vers intercept prédit
  - Contact (ballEta < 0.4s) :
    - z < 250u → Dodge plat
    - z < 400u → Saut + Dodge
    - z ≥ 400u → Saut + Boost + Dodge (aérien)
- Sinon → `Drive(ShadowPosition)` _(60% entre notre but et la balle)_

#### CONTESTED _(ETAs proches, écart < 0.3s)_
- Zone Offensive _(balle dans leur moitié)_  →  `FindShot(LeurBut)` | `Drive(Balle)`
- Zone Défensive _(balle dans notre moitié)_
  - Temps avant que la balle passe à < 400u de moi < 1.5s → `Fifty` _(classe RedUtils/Actions/Fifty.cs)_
  - Sinon → `Drive(Balle)`
  - Sinon  →  `Drive(Balle)`

#### POSSESSED _(mon ETA < leur ETA − 0.3s — on a la balle)_
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
