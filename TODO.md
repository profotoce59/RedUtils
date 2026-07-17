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

## À faire

- [ ] **Défense active** — en état Defensive, Player2 devrait se positionner pour bloquer les tirs (entre balle et notre but) plutôt que juste se mettre derrière Player1
- [ ] **Gestion du boost en jeu** — collecter en se repliant (pas seulement au kickoff)
- [ ] **QuickShot** — utiliser pour les tirs faciles à courte distance (non utilisé actuellement)
- [ ] **HalfFlip** — utiliser pour les demi-tours rapides
- [ ] **Wavedash** — utiliser au lieu de SpeedFlip si pas de boost
- [ ] **Action démo** — le bot va démolir le joueur adverse le plus proche
- [ ] **Démo offensive 2v1** — P1 va démolir, P2 tire dans le but vide
