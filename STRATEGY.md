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
| `Drive(Pressing)` | Conduite vers le point à 1100u goal-side de la balle — vient presser le porteur adverse |
| `Drive(Contest)` | Conduite vers le point à 500u goal-side de la balle — l'Attacker challenge le porteur en défense (2v2, Support couvre) |
| `Shot→Save` / `Drive→Save` | Priorité défensive absolue : la balle va rentrer dans notre but, tir de dégagement ou interception d'urgence |
| `Shot→Dégagement` | Balle dangereuse dans notre tiers (Attacker) : tir loin de notre but |
| `Drive→GoalSide` | Repli entre la balle et notre but avant tout contact — anti-CSC |
| `FindShot(LeurBut)` | Cherche un tir vers le but adverse |
| `FindShot(Dégagement)` | Cherche un tir pour dégager loin de notre but |
| `GetBoost` | Va chercher le meilleur gros boost disponible |
| `Kickoff` | Speedflip vers la balle au kickoff |
| `Dodge(Balle)` | Dodge en direction de la balle (50/50) |

---

## Calcul de l'état de possession

ETA = temps avant que le joueur puisse intercepter un slice de `Ball.Prediction`, via `Drive.GetEta`.

> **`Drive.GetEta` — modèle d'accélération.** Le calcul intègre la vraie courbe d'accélération au sol
> depuis la vitesse **actuelle** de la voiture (`Drive.TimeToCoverDistance`) : throttle de 1600 uu/s²
> à l'arrêt jusqu'à 160 à 1400 uu/s (0 au-delà de 1410), plus 991,667 tant qu'il reste du boost,
> consommé à 33,3/s, plafond 2300.
> Auparavant un `MathF.Max(..., 1400)` supposait la voiture **déjà lancée à 1400 uu/s** — aucun modèle
> d'accélération au sol n'existait. Une voiture à l'arrêt était donc créditée d'une vitesse qu'elle
> met ~0,8 s à atteindre : sur DEF1 l'ETA annonçait 1,11 s pour un trajet en demandant 1,45 s, et le
> bot s'engageait sur des interceptions physiquement hors de portée (il « se faisait outspeed »).
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

### Priorités défensives (`TryDefensivePriority`, tous rôles, avant tout le reste)
Évaluées à chaque tick avant la logique standard (Support ou Attacker). Contrôlées par les flags de `Fixes.cs`.

1. **SAVE** _(si `Fixes.DefensiveOverhaul`, tous rôles)_ — `Ball.Prediction.FindGoal(adversaire)` voit la balle franchir NOTRE ligne :
   - `FindShot(OurGoal, shootAwayFromGoal: true)` → `Shot→Save` si un tir est jouable
   - Sinon, interception d'urgence sur la trajectoire (boost autorisé, `wasteBoost: true`) → `Drive→Save`.
     La cible est la **dernière** slice atteignable avant la ligne de but (`FindLatestInterceptableSlice`),
     pas la première : `Ball.Prediction.Find` balaie du plus tôt au plus tard et s'arrête à la première
     slice tout juste atteignable — un point structurellement à marge nulle, où la moindre erreur de
     `Drive.GetEta` fait rater le save. En cherchant depuis la ligne de but vers l'arrière, la dernière
     slice atteignable est plus proche du but : moins de distance à couvrir, plus de temps accordé.
2. **DÉGAGEMENT** _(Attacker uniquement, zone défensive)_ — déclenché si la balle est **dangereuse** : elle fonce vers notre but (`headingToUs`), OU elle est dans notre tiers **et** contestable (un adversaire à < 2500u d'elle). Une balle simplement posée sans adversaire proche n'est **pas** dégagée (cas DEF5 — on la contrôle). → `FindShot(Dégagement)` → `Shot→Dégagement`
   - **Sauf si `NotPossessed`** _(adversaire arrive > 0.4s avant nous, souvent il contrôle déjà la balle)_ : un dégagement suppose d'atteindre la balle en premier. `FindShot` ignore les touches adverses et s'accrocherait à un slice lointain que l'adversaire aura frappé bien avant (Shot→Dégagement fantôme). On saute alors le dégagement et on laisse la logique NotPossessed (`Drive→Contest` / `Fifty`) se rapprocher pour disputer le 50/50.
3. **GOAL-SIDE** _(Attacker uniquement, zone défensive, anti-CSC)_ — si on n'est pas entre la balle et notre but, on s'y replace AVANT tout contact (`Drive→GoalSide`) plutôt que de pousser la balle vers notre propre but en la poursuivant

Le Support garde toujours sa couverture (jamais concerné par 2 et 3).

**Latch des tirs** : un `Shot` en cours n'est pas resélectionné tant que l'intent ne change pas
(`ShotInProgress`). Un `Shot` gère son propre cycle de vie — il rafraîchit sa cible toutes les 0.2s
et s'auto-abandonne via ses gardes internes. Rappeler `FindShot` à chaque tick jetait cet état et
imposait une cible choisie à froid, qui peut flip-flop d'un tick à l'autre sur les cas limites.

---

### Attribution des rôles (`ComputeRole`)
Score = ETA vers la balle + pénalité de 2s si l'angle car→balle→leur but dépasse 108°. Score le plus bas = Attacker.
- **Départage d'égalité** : à score strictement égal (kickoff symétrique), l'index le plus bas est Attacker — sinon les deux bots se croient Attacker.
- **Hystérésis (0.3s)** : le titulaire garde son rôle tant que l'autre ne le bat pas de 0.3s. Les deux conditions sont complémentaires, donc les deux bots restent d'accord sans état partagé.

### SUPPORT _(coéquipier présent et mon score de rôle > celui du coéquipier)_
- État `NOT POSSESSED` **en zone défensive** _(ils ont la balle dans notre moitié)_ → `Arrive(DefensivePosition)` face à la balle — dernier homme, il couvre le but **boost ou pas**
  - En zone **offensive**, pas de repli : le Support monte en soutien (`Arrive(BackupPosition)` ci-dessous) pour servir de relais au pressing au lieu d'abandonner le terrain
- Collecte de boost _(hystérésis : entre si boost < 30, sort à ≥ 60)_ → `GetBoost` limité aux **gros pads goal-side de la balle** ; s'il n'y en a aucun, on se replace sans boost plutôt que de traverser le terrain
- Sinon → `Arrive(BackupPosition)` face à la balle

`BackupPosition` = 2500u goal-side de la **balle** (pas de l'attaquant, qui transmettrait ses erreurs de placement), décalée de 800u vers le poteau **opposé** à la balle (back post — deux bots jamais sur la même ligne), bornée au terrain (marge 400u).

Le décalage back post est une **rampe** sur ±1200u autour de `x = 0`, pas un `Sign()` : avec un signe, la cible saute de 1600u dès que la balle frôle l'axe central, ce qui dépasse le seuil de re-ciblage et recrée l'`Arrive` en boucle avec une direction inversée — le Support tourne alors en rond au lieu de se placer.

**Latch des actions** : `Drive`/`Arrive`/`GetBoost` ne sont **pas** recréés à chaque tick (la cible est mutée si elle dérive de < 800u). Recréer un `Drive` remet son `timeOnGround` à zéro, ce qui interdit dodges/speedflips/wavedashes (`Drive.cs:160` exige 0.2s au sol) — c'était la cause des replacements lents.

**Usage du boost (`wasteBoost`)** : un `Drive` ne boost et ne speedflip **que** si créé avec `wasteBoost: true` (`Drive.cs:143` coupe le boost sinon, `Drive.cs:170` idem pour le speedflip) — throttle seul plafonne à `MaxThrottleSpeed` (~1400 uu/s). Les déplacements où la vitesse gagne le duel sont donc en **full-send** : `Drive→Pressing`, `Drive→Contest`, `Drive→Balle`, `Drive→GoalSide` (+ `Drive→Save`). Les placements de temporisation restent **cadencés** (boost gardé pour le duel) : `Drive→Shadow`, `Drive→Contour`, et les `Arrive` de replacement (`BackupPos`, `Couverture`).

---

### ATTACKER _(coéquipier présent et mon score de rôle ≤ celui du coéquipier)_ / SOLO _(pas de coéquipier)_

#### NOT POSSESSED _(ils ont la balle)_
- Temps avant que la balle passe à < 400u de moi < 1.5s → `Fifty` _(RedUtils/Actions/Fifty.cs)_
  - Approche : Drive vers intercept prédit
  - Contact (ballEta < 0.4s) :
    - z < 250u → Dodge plat
    - z < 400u → Saut + Dodge
    - z ≥ 400u → Saut + Boost + Dodge (aérien)
- Sinon, zone **Offensive** _(balle dans leur moitié)_ → `Drive(Pressing)` — on vient à 1100u goal-side de la balle mettre la pression ; le `Fifty` ci-dessus prend le relais au contact.
  _Le shadow à 60% depuis notre but placerait le bot au rond central : c'est un placement défensif, absurde quand la balle est chez eux._
  _La cible est calculée depuis `Ball.Location` (fonction continue) et **non** depuis un slice d'interception : un slice atteignable saute de plusieurs milliers d'unités d'un tick à l'autre, ce qui casse le latch de `SetDrive`, recrée le `Drive` et remet son `timeOnGround` à zéro — plus aucun dodge ni speedflip, le bot traverse le terrain à vitesse de base. Les dodges sont laissés **activés** ici : c'est un déplacement longue distance, pas une approche de contact._
- Sinon, zone **Défensive** _(balle dans notre moitié)_ :
  - **2v2** _(coéquipier vivant → rôle Attacker)_ → `Drive(Contest)` — on ferme sur le porteur pour le contester ; le Support couvre déjà le but (`DefensivePosition`), donc le premier homme peut challenger. Le `Fifty` prend le relais au contact.
    - **Cible = ligne balle→notre but** (`ContestPoint`) : on se place **sur l'axe balle→NOTRE but**, goal-side de la balle, à un standoff. On projette le long de cet axe **invariant** (l'axe dangereux), **pas** du cap du porteur : son cap est volatile — il peut tourner et couper la balle **derrière nous** vers le but pendant qu'on court vers son ancienne direction. En tenant la ligne du but, on est déjà devant lui sur l'axe qui compte ; il ne peut aller que sur les côtés → **on le repousse vers le corner**.
      - **Standoff anticipé** : `standoff = min(avancée_anticipée, ContestMaxAdvance=1800u) + ContestGap(500u)`. L'avancée n'est calculée que sur la composante de vitesse **vers le but** (le latéral vers le corner est ignoré, on ne le suit pas) ; si l'adversaire contrôle/est près (< `ContestCarryDistance`=500u) on l'anticipe **avec accélération boost** (`ReachDistance`), sinon vitesse constante. Le standoff **s'auto-ajuste** : loin (grand ETA, borné `ContestMaxLead`=2.5s) on contient profond sur la ligne ; près on ferme pour challenger. Le `Fifty` prend le relais au contact.
  - **Solo** _(pas de coéquipier)_ → `Drive(ShadowPosition)` _(60% entre notre but et la balle)_ : sans couverture derrière, on contient au lieu de challenger.

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
