# TODO — HardcodeBot

## Fait

- [x] **Rotation 2v2** — `ComputeRole` dans `Rotation.cs` : ETA + pénalité d'angle (108°) pour désigner Attacker/Support
- [x] **Comportement Player2** — GetBoost si boost < 70, sinon BackupPosition (1500u derrière Player1)
- [x] **3 états de jeu** — `ComputeGameState` : Offensive / Contested / Defensive selon comparaison ETA
  - Defensive : drive vers balle (ferme le gap)
  - Contested : FindShot(TheirGoal) ou drive vers balle
  - Offensive + adversaire a touché : dégagement (`Target(OurGoal, shootAwayFromGoal: true)`)
  - Offensive + nous avons touché : FindShot(TheirGoal)
- [x] **Bugs FindShot corrigés** — gap hauteur 270-299, double construction shot, division par zéro ShotValid, formule vélocité unifiée
- [x] **AccurateShotCheck** — implémentation physique RocketSim pour comparer la précision vs DefaultShotCheck
- [x] **Défense active** (`Fixes.DefensiveOverhaul`) — `TryDefensivePriority` dans `Bot.cs` : save si la prédiction voit la balle rentrer, dégagement en zone dangereuse, repli goal-side anti-CSC. Voir `STRATEGY.md`
- [x] **Pressing offensif** (`Fixes.OffensivePressing`) — en `NotPossessed` + balle chez l'adversaire, on conteste au lieu de se replier. Voir `STRATEGY.md`
- [x] **Latch des tirs** — `ShotInProgress` : un `Shot` en cours n'est plus recréé à chaque tick (il gère son propre cycle de vie : rafraîchit sa cible toutes les 0.2s et s'auto-abandonne)
- [x] **Bug save DEF1 (`GetEta` optimiste au démarrage)** — `minSpeed = MathF.Max(..., 1400)` supposait la voiture déjà lancée à 1400 uu/s : aucun modèle d'accélération au sol n'existait dans le projet. Sur DEF1 (voiture immobile au spawn) l'ETA annonçait 1.11s pour un trajet en demandant ~1.45s → le tir n'était jouable à aucun moment. Remplacé par une intégration de la vraie courbe d'accélération (`Drive.TimeToCoverDistance`)
- [x] **Bug save (interception `Arrive→Save`)** — trois défauts liés :
  1. **Slice ciblée** : la PREMIÈRE atteignable (`Ball.Prediction.Find`) est à marge nulle (fragile) ; la DERNIÈRE attend la balle devant la cage (trop passif). `FindSaveInterceptSlice` prend la **plus tôt avec une marge de confort** (`SaveInterceptMargin`=0.1s), **souple** (repli sur la plus tôt atteignable si la balle est trop rapide). Atteignabilité via `Movement.EtaFor` (`InterceptSlack`).
  2. **Point de contact** déduit de la **direction de la balle** (`GoalSideContact`) pour la bloquer de face, repli « vers notre but » si balle lente (< `SlowBallSpeed`).
  3. **Exécution** : `Arrive` **sans direction d'arrivée** (pacing seul) au lieu d'un `Drive` full-send — le Drive fonçait et dépassait la balle ; la mise en ligne d'`Arrive` avec direction plantait le point d'approche dans le filet
  
---

## À faire — Joueur Support _(audit du 19/07/2026, dans cet ordre)_

- [x] **S1 — Logger le Support** — sélection d'action extraite dans `SelectAction()`, le log tourne pour les deux bots, index ajouté (`MyBot#2`/`#3`)
- [x] **S2 — Ne plus recréer `Drive`/`GetBoost` à chaque tick** — helpers `SetDrive`/`SetArrive` : mutation de la cible si dérive < 800u, recréation sinon ; `GetBoost` latché
- [x] **S3 — Contraindre `GetBoost` à la zone goal-side** — gros pads goal-side de la balle uniquement ; aucun pad sûr → replacement sans boost
- [x] **S4 — Hystérésis sur le seuil de boost** — entre en collecte sous 30, en sort à ≥ 60
- [x] **S5 — `BackupPosition` basée sur la balle** — 2500u goal-side de la balle + décalage back post 800u + clamp terrain (marge 400u)
- [x] **S6 — Conditionner le Support au `gameState`** — en `NotPossessed` : `Arrive(DefensivePosition)` dernier homme, boost ou pas
- [x] **S7 — Utiliser `Arrive` au lieu de `Drive`** — le Support arrive face à la balle (Couverture et BackupPos)
- [x] **S8 — Stabiliser `ComputeRole`** — hystérésis 0.3s en faveur du titulaire (conditions complémentaires entre les deux bots) + départage d'égalité sur `Index`

### Cas de test associés _(à ajouter dans `state_setting_tests.py`)_
T1 attaquant engagé / T2 back post / T3 boost dans le mauvais sens / T4 contre-attaque dernier homme / T5 échange de rôle / T6 kickoff symétrique

---

## À faire — Divers

- [ ] **Gestion du boost en jeu** — collecter en se repliant (pas seulement au kickoff)
- [ ] **QuickShot** — utiliser pour les tirs faciles à courte distance (non utilisé actuellement)
- [ ] **HalfFlip** — utiliser pour les demi-tours rapides
- [x] **Wavedash dans Drive** — remplace le Dodge sur les courses au sol. Variante boostée (`new Wavedash(dir, boost:true)`) si `Boost>30` (activation dès v0=800), sinon sans-boost (dès v0=1000). Durée de non-dispo = `Duration` (~0.9s boosté / ~0.97s sans)
- [ ] **Modèle wavedash pour `GetEta`/`Movement`** — fonction statique `WavedashModel(v0, boost) → (time, dist)` : `time` ≈ linéaire en v0 (quasi constant), `dist` = polynôme en v0 (accél. plafonne au supersonique). Calibrer sur les mesures du banc (voir le bloc `TODO(GetEta)` dans `Wavedash.cs`). Aujourd'hui `GetEta` ne modélise pas le gain du wavedash → pessimiste quand un wavedash serait joué
- [ ] **Action démo** — le bot va démolir le joueur adverse le plus proche
- [ ] **Démo offensive 2v1** — P1 va démolir, P2 tire dans le but vide
