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

- [ ] **Défense active** — en état Defensive, Player2 devrait se positionner pour bloquer les tirs (entre balle et notre but) plutôt que juste se mettre derrière Player1
- [ ] **Gestion du boost en jeu** — collecter en se repliant (pas seulement au kickoff)
- [ ] **QuickShot** — utiliser pour les tirs faciles à courte distance (non utilisé actuellement)
- [ ] **HalfFlip** — utiliser pour les demi-tours rapides
- [ ] **Wavedash** — utiliser au lieu de SpeedFlip si pas de boost
- [ ] **Action démo** — le bot va démolir le joueur adverse le plus proche
- [ ] **Démo offensive 2v1** — P1 va démolir, P2 tire dans le but vide
