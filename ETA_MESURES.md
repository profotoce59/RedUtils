# Étalonnage de l'ETA — journal de mesures

Registre des mesures du banc `Fixes.EtaBench` (voir `state_setting_tests_eta.py`).
Objectif : rendre fiable « temps pour aller de A à B », brique de base du moteur de
déplacement. Tant que ce calcul est faux, tout ce qui s'appuie dessus — validité des tirs,
état de possession, attribution des rôles, choix du boost pad — hérite de l'erreur.

**Règle de ce fichier : on sépare les mesures (faits) des analyses (déduites).** Toute
valeur calculée porte la mention de sa méthode, pour qu'on sache ce qui est observé et ce
qui est reconstruit.

## Reproduire

1. `RedUtils/Fixes.cs` → `EtaBench = true`
2. `dotnet build Bot.sln`
3. Match RLBot avec 4 voitures, state setting activé
4. `python state_setting_tests_eta.py`, dérouler les scénarios un par un

Lecture : `erreur > 0` = ETA **optimiste** (le bot s'engage sur des trajets hors de
portée) ; `erreur < 0` = **pessimiste** (il refuse des tirs jouables).

---

# Session 1 — modèle initial

**État du code au moment de ces mesures :** `Drive.GetEta` contenait encore le
`DodgeDistanceBonus` de 250 uu. La colonne `prévu` reflète donc l'ancien modèle.

## Mesures brutes

| # | cas | dist | v0 | boost0 | angle | prévu | réel | erreur | % | vArrivée | boost restant |
|---|---|---|---|---|---|---|---|---|---|---|---|
| E1 | droit devant | 309 | 43 | 100 | 0° | 0,506 | 0,554 ¹ | +0,048 | +9 % | 878 | 85 |
| E2 | droit devant | 1502 | 1 | 100 | 0° | 1,176 | 1,341 | +0,165 | +14 % | 1668 | 58 |
| E3 | droit devant | 2501 | 1 | 100 | 0° | 1,667 | 1,990 | +0,323 | +19 % | 1640 | 71 |
| E5 | sans boost | 1502 | 27 | 0 | 0° | 1,563 | 1,809 | +0,246 | +16 % | 1158 | 0 |
| E6 | boost partiel | 1502 | 1 | 31 | 0° | 1,194 | 1,403 | +0,209 | +17 % | 1350 | 0 |
| E7 | virage | 1502 | 1 | 100 | 90° | 1,716 | 1,506 | −0,210 | −12 % | 1702 | 52 |
| E8 | virage | 1502 | 2 | 100 | 90° | 1,653 | 1,505 | −0,148 | −9 % | 1705 | 52 |
| E9 | demi-tour | 1502 | 2 | 100 | 180° | 1,776 | 1,816 | +0,039 | +2 % | 1150 | **100** |
| E10 | lancé | 2478 | 1391 | 100 | 0° | 1,149 | 1,294 | +0,145 | +13 % | 2019 | 83 |
| E11 | lancé au plafond | 2463 | 2291 | 0 | 0° | 0,965 | 1,109 | +0,143 | +15 % | 2012 | 0 |

¹ E1 a été mesuré **avant** l'ajout de l'extrapolation de fin de trajet. Le chrono brut
valait 0,450 s sur un rayon d'arrivée de 100 uu ; 0,554 est la valeur reconstruite. Sur un
trajet de 309 uu ces 100 uu représentent un tiers du parcours — **c'est la mesure la moins
fiable de la table**, à ne pas surinterpréter.

## Analyses

### 1. Le bonus de dodge est faux — prouvé, pas supposé

E11 est le cas décisif : parcourir 2463 uu en partant de 2291 uu/s **sans boost**. Le
plafond de vitesse étant 2300, le minimum physique absolu est :

```
2463 / 2300 = 1,071 s
```

Le modèle annonçait **0,965 s**. Une prédiction que la physique interdit. Cause : le
`DodgeDistanceBonus` de 250 uu était retranché sans condition — y compris au plafond de
vitesse, où un dodge ne peut rien rapporter et ne fait que coûter sa récupération.

### 2. Le modèle d'accélération est bon

En retirant ce bonus, les lignes droites se resserrent brutalement (recalcul par
réimplémentation de `TimeToCoverDistance`, validée sur E11 : la réimplémentation reproduit
le 0,966 du C# au millième) :

| # | prévu sans bonus | réel | erreur |
|---|---|---|---|
| E2 | 1,311 | 1,341 | **+2 %** |
| E5 | 1,760 | 1,809 | **+3 %** |
| E6 | 1,356 | 1,403 | **+3 %** |
| E10 | 1,257 | 1,294 | **+3 %** |
| E11 | 1,075 | 1,109 | **+3 %** |
| E3 | 1,776 | 1,990 | **+12 %** ← à part |

5 cas sur 6 à +2/+3 %, sur toute la plage : arrêt, lancé à 1400, lancé à 2300, boost
0 / 31 / 100. `Drive.TimeToCoverDistance` est **validé** et doit être conservé.

### 3. E3 : le contrôleur ne conduit pas au minimum

E3 n'est pas une erreur de modèle. `vArrivée = 1640` au lieu de 2300, et **29 de boost
consommé en 2 s**. `Drive` a speedflippé au lieu de booster, et est arrivé **plus
lentement**. Le modèle décrivait un trajet optimal que la voiture ne conduit pas.

→ Décision : l'ETA doit décrire **ce que la voiture fait vraiment**, flip compris, plutôt
qu'un optimum théorique inatteignable.

### 4. Le virage est surfacturé

Décomposition du terme de virage à 90° depuis l'arrêt :

```
vitesse de virage estimée = 446 uu/s  →  rayon 239  →  arc 376 uu
temps facturé à l'arc     = 376 / 446 = 0,844 s
```

0,844 s pour le seul arc, sur un trajet mesuré à 1,506 s au total. E7/E8 donnent −9/−12 %
**avec** le bonus de dodge ; sans lui l'écart se creuse encore.

⚠️ Contrairement aux lignes droites, la réimplémentation Python **ne reproduit pas
fidèlement** la géométrie de virage du C# (`GetDistance` déplace le centre de rotation).
Les valeurs « sans bonus » pour E7/E8 doivent être **re-mesurées**, pas calculées.

### 5. Le boost n'est pas utilisé en virage

E9 (180°) finit avec `boostRestant = 100` : **la voiture n'a pas boosté du tout**. La
porte `angleToTarget < 0.3` de `Drive.cs:143` l'interdit tant qu'elle n'est pas alignée.
Or l'ETA suppose le boost dépensé dès t = 0.

Sur E9 cette erreur (modèle trop rapide) compense celle du virage (modèle trop lent), d'où
un +2 % trompeur. **E9 n'est pas exploitable pour l'étalonnage du virage.**

## Conclusion de la session 1

Deux erreurs de signe opposé, qui se compensaient partiellement — d'où leur invisibilité
en match pendant des heures de débogage :

| erreur | ampleur | cause | statut |
|---|---|---|---|
| lignes droites | **+15 %** optimiste | bonus de dodge inconditionnel | **corrigé** (M1) |
| virages | **−10 %** pessimiste | arc surfacturé + boost supposé | à étalonner (M3/M4) |
| E3 seul | +12 % | le contrôleur flippe au lieu de booster | à modéliser (M3) |

---

# Session 2 — après retrait du bonus de dodge

**État du code :** `DodgeDistanceBonus` retiré de `Drive.GetEta`. Banc étendu avec le
compteur de flips et le canal « sans dodge ».

Attendu : les lignes droites passent à ~+3 %. Les virages restent à étalonner.

## Distance (droit devant, arrêt, boost 100)

| # | dist | flips | prévu | réel | erreur | % | vArrivée | boost restant |
|---|---|---|---|---|---|---|---|---|
| D1 | 505 | 0 | 0,676 | 0,718 | +0,042 | +6 % ¹ | 1105 | 79 |
| D2 | 1003 | 0 | 1,030 | 1,066 | +0,036 | +3 % | 1431 | 67 |
| D3 | 1502 | 0 | 1,310 | 1,341 | +0,031 | +2 % | 1668 | 70 |
| D4 | 2001 | 0 | 1,554 | 1,583 | +0,029 | +2 % | 1890 | 50 |
| D5 | 2501 | **1** | 1,776 | 2,015 | +0,239 | **+13 %** | 1468 | 75 |
| D6 | 3001 | **1** | 1,991 | 2,234 | +0,244 | **+12 %** | 1890 | 74 |

¹ Trajet court : l'extrapolation des 100 derniers uu pèse un cinquième du parcours. Mesure
la moins fiable de la série, à ne pas surinterpréter.

## Les mêmes, sans dodge — coût réel du flip par différence

| # | dist | flips | prévu | réel | erreur | % | vArrivée | boost restant | Δ avec D |
|---|---|---|---|---|---|---|---|---|---|
| N4 | 2001 | 0 | 1,554 | 1,583 | +0,029 | +2 % | 1890 | 50 | **0,000** (témoin) |
| N5 | 2501 | 0 | 1,776 | 1,808 | +0,033 | +2 % | 2032 | 56 | **+0,207** |
| N6 | 3001 | 0 | 1,993 | 2,027 | +0,035 | +2 % | 2022 | 45 | **+0,207** |
| N7 | 4001 | 0 | 2,428 | 2,474 | +0,046 | +2 % | 2036 | 68 | — |
| N8 | 5001 | 0 | 2,862 | 2,912 | +0,050 | +2 % | 2018 | 45 | — |

## Angle (1500 uu, arrêt, boost 100)

| # | angle | flips | prévu | réel | erreur | % | vArrivée | boost restant |
|---|---|---|---|---|---|---|---|---|
| A1 | 30° | 0 | 1,385 | 1,354 | −0,030 | −2 % | 1677 | 57 |
| A2 | 45° | 0 | 1,464 | 1,370 | −0,095 | −6 % | 1675 | 57 |
| A3 | 60° | 0 | 1,579 | 1,392 | −0,188 | −12 % | 1677 | 56 |
| A4 | 90° | 0 | 1,853 | 1,506 | −0,347 | **−19 %** | 1702 | 52 |
| A5 | 135° | 0 | 2,126 | 2,086 | −0,041 | −2 % | 1789 | 45 |
| A6 | 180° | 0 | 1,776 | 1,815 | +0,039 | +2 % | 1150 | **100** |
| B4 | 90° sans dodge | 0 | 1,853 | 1,506 | −0,347 | −19 % | 1702 | 52 |
| B6 | 180° sans dodge | 0 | 1,776 | 1,816 | +0,039 | +2 % | 1150 | **100** |

B4/B6 sont **identiques au millième** à A4/A6 : aucun flip n'est déclenché en virage, donc
le canal « sans dodge » n'y change rien. Physique déterministe, mêmes conditions initiales,
même résultat — ça valide au passage la reproductibilité du banc.

## Boost et vitesse initiale

| # | cas | flips | prévu | réel | erreur | % | vArrivée | boost restant |
|---|---|---|---|---|---|---|---|---|
| C1 | 2500 uu, boost 24 | **1** | 2,121 | 1,990 | −0,132 | **−6 %** | 1646 | 7 |
| C2 | 2500 uu, boost 31 | **1** | 1,999 | 2,013 | +0,014 | +1 % | 1480 | 5 |
| C3 | 2500 uu, boost 100 | 0 | 1,776 | 1,808 | +0,033 | +2 % | 2032 | 56 |
| V1 | lancé à 991 | **1** | 1,400 | 1,518 | +0,119 | +8 % | 1723 | 86 |
| V2 | lancé à 1391 | 0 | 1,257 | 1,294 | +0,036 | +3 % | 2019 | 71 |
| V3 | lancé à 2291, boost 12 | 0 | 1,070 | 1,107 | +0,037 | +3 % | 2061 | 11 |
| V4 | lancé à 1391 **à l'envers** | 0 | 1,323 | 1,769 | +0,446 | **+34 %** | 1774 | 47 |

---

# Analyses de la session 2

## 1. Le modèle d'accélération est confirmé : +2 % sur toute la plage

Tous les trajets **sans flip** tombent entre +2 % et +3 % : 500 → 5000 uu, vitesse initiale
0 / 991 / 1391 / 2291, boost 12 / 24 / 31 / 100. Treize mesures concordantes.

Le biais résiduel de +2 % est régulier et va toujours dans le même sens (le réel est un
peu plus lent) : c'est le contrôleur qui n'est pas parfaitement optimal — micro-corrections
de direction, throttle proportionnel. **Rien à corriger ici, `TimeToCoverDistance` est
bon.**

## 2. Le flip coûte 0,207 s — mesuré, pas déduit

C'est le résultat le plus net de la session, obtenu par **différence directe** à distance
égale :

| distance | avec flip | sans flip | écart |
|---|---|---|---|
| 2501 uu (D5/N5) | 2,015 | 1,808 | **+0,207 s** |
| 3001 uu (D6/N6) | 2,234 | 2,027 | **+0,207 s** |
| 2001 uu (D4/N4) | 1,583 | 1,583 | 0,000 (témoin, aucun flip) |

Deux mesures indépendantes au millième près. Le témoin à 2000 uu, où aucun flip n'est
déclenché des deux côtés, confirme que le canal ne perturbe rien par lui-même.

**Le flip coûte doublement**, et la seconde raison est moins évidente :

| | boost consommé |
|---|---|
| 2500 uu avec flip | 25 |
| 2500 uu sans flip | 44 |
| 3000 uu avec flip | 26 |
| 3000 uu sans flip | 55 |

Pendant le flip la voiture ne boost pas — elle perd donc du temps *et* renonce à
l'accélération qu'elle aurait pu prendre. `vArrivée` le confirme : 1468 avec flip contre
2032 sans.

### Quand `Drive` déclenche-t-il un flip ?

| condition | observation |
|---|---|
| distance depuis l'arrêt | flips=0 à 2001 uu, flips=1 à 2501 uu → **seuil entre les deux** |
| vitesse initiale élevée | V2 (1391) et V3 (2291) : flips=0 — la garde `carSpeed < 2000` de `Drive.cs:157` referme la fenêtre |
| vitesse initiale moyenne | V1 (991) : flips=1 |
| en virage | A1→A6 : flips=0 partout |

## 3. Le flip n'est pas toujours néfaste — il dépend du boost

C1 (boost 24, flip) est **plus rapide que prévu de 6 %**, alors que D5 (boost 100, flip)
est plus lent de 13 %. La différence : C1 finit avec 7 de boost, D5 avec 75.

Une fois le boost épuisé, le flip est le **seul** moyen de gagner de la vitesse — il
devient rentable. Quand le boost coule à flots, il ne fait que gêner.

→ Le modèle de flip ne peut pas être une pénalité fixe. Il doit dépendre du boost
disponible **sur la durée du trajet**, pas seulement au départ.

## 4. Le virage : erreur maximale à 90°, qui s'annule aux grands angles

| angle | 30° | 45° | 60° | **90°** | 135° | 180° |
|---|---|---|---|---|---|---|
| erreur | −2 % | −6 % | −12 % | **−19 %** | −2 % | +2 % |

L'erreur croît régulièrement jusqu'à 90°, puis **s'effondre**. La colonne `boost restant`
donne la clé :

- à 90° : 52 de boost restant → la voiture a boosté, seul le surcoût d'arc s'exprime → −19 % ;
- à 180° : **100 de boost restant, la voiture n'a pas boosté du tout**. La porte
  `angleToTarget < 0.3` (`Drive.cs:143`) le lui a interdit sur toute la manœuvre.

À 180°, deux erreurs opposées se compensent : le modèle suppose le boost dépensé (trop
rapide) et surfacture l'arc (trop lent). Le +2 % est un **artefact**, pas une réussite.

→ Confirmation directe du plan : la phase de virage doit être modélisée **sans boost**, en
plus de la correction de l'arc. Traiter l'un sans l'autre déplacerait l'erreur de 180°
au lieu de la résoudre.

## 5. V4 : le freinage n'est pas modélisé du tout — +34 %

Le plus gros écart de la table. La voiture fait face à la cible mais **s'en éloigne** à
1391 uu/s.

Dans `GetEta`, `currentSpeed = carForward · velocity` vaut **−1391**. Puis
`Cap(SpeedAfterTurn(−1391, ~0), 0, MaxSpeed)` écrase ce négatif à **0** : le modèle traite
la voiture comme si elle était à l'arrêt, alors qu'elle doit d'abord s'arrêter.

Reconstruction (freinage à `Car.BrakeAccel` = 3500 uu/s²) :

```
freinage 1391 → 0        : 0,397 s, en reculant de 276 uu
puis 1525 + 276 = 1801 uu depuis l'arrêt, boost 100 : 1,461 s
                                             total : 1,858 s
```

Mesure : **1,769 s**. Modèle actuel : 1,323 s. La reconstruction explique l'écart (elle
surestime un peu, la voiture ne freinant probablement pas à pleine puissance tout du long).

→ Une vitesse initiale **négative** doit être payée : temps de freinage + distance perdue.
C'est le cas le plus fréquent en défense — un bot qui recule et doit repartir vers l'avant.

## 6. Question ouverte : plateau de vitesse vers 2020

`vArrivée` plafonne autour de **2020** sur tous les longs trajets — N6 2022, N7 2036,
N8 2018, C3 2032, V2 2019, V3 2061 — alors que le modèle table sur le plafond de 2300, et
qu'il reste du boost (N8 finit avec 45).

Le total reste pourtant juste à +2 % (5001 uu : 2,862 prévu / 2,912 mesuré), ce qui suggère
que le modèle sous-estime l'accélération au début et la surestime à la fin, les deux se
compensant.

**Non élucidé.** À investiguer avant de faire confiance au modèle sur les très longs
trajets. Piste : `vArrivée` est relevée à 100 uu de la cible, où la voiture peut déjà
ralentir — ce n'est pas nécessairement la vitesse maximale atteinte.

## Synthèse — ce qui reste à corriger dans `Movement.cs`

| # | erreur | ampleur | correctif |
|---|---|---|---|
| 1 | ligne droite sans flip | +2 % | **rien à faire**, modèle validé |
| 2 | flip | +0,207 s quand le boost abonde, gain quand il manque | pénalité **conditionnée au boost**, seuil de déclenchement entre 2000 et 2500 uu et fenêtre fermée au-dessus de 2000 uu/s |
| 3 | virage | jusqu'à −19 % à 90° | réduire l'arc **et** supprimer le boost pendant la phase de virage |
| 4 | vitesse initiale négative | +34 % | facturer le freinage (3500 uu/s²) et la distance perdue |
| 5 | plateau à ~2020 | inexpliqué | à investiguer |

---

# Session 3 — validation de `Movement.cs` (hors ligne)

Le modèle de `Bot/Movement.cs` rejoué **contre les 22 mesures de la session 2**, par
réimplémentation Python de sa logique. Ce n'est pas une mesure en jeu : c'est une
vérification que les constantes étalonnées reproduisent bien les mesures dont elles sont
tirées. La confirmation en jeu reste à faire.

| | sans `ControllerLoss` | avec `ControllerLoss = 1,022` |
|---|---|---|
| cas sous 5 % | 20/22 | 20/22 |
| dispersion du gros du lot | +1,5 % à +3,4 % (**optimiste**) | −0,7 % à +1,1 % (centré) |
| écarts restants | D1 +5,6 %, C1 −6,2 % | C1 −8,2 %, V4 −6,9 % (**pessimistes**) |

Le facteur retenu ne change pas le nombre de cas conformes mais **le sens de l'erreur
résiduelle**. Sans lui le modèle est systématiquement optimiste : c'est la direction qui
fait s'engager le bot sur des interceptions hors de portée — le défaut d'origine. Avec, il
devient légèrement pessimiste, ce qui coûte au pire un tir refusé.

Détail après correction : les 18 cas du gros du lot tiennent dans **±1,5 %**, dont les six
angles (A1→A5) à **±0,7 %** et les cinq distances longues (N5→N8) à **±0,5 %**.

## Écarts assumés

**C1, −8 % — le flip qui fait gagner du temps.** À court de boost (24), le flip est le seul
moyen d'accélérer : la voiture va 0,13 s plus vite que sans. Le modèle sait ne pas
facturer de pénalité dans ce cas, mais pas encore créditer un gain. **Une seule mesure**
soutient ce phénomène — en fabriquer d'autres avant de modéliser quoi que ce soit.

**V4, −7 % — le freinage est surestimé.** La reconstruction suppose un freinage à pleine
puissance (3500 uu/s²) sur toute la décélération, ce que la voiture ne fait probablement
pas. Erreur dans le sens sûr.

**D1** (trajet de 500 uu) rentre dans les clous après correction, mais reste la mesure la
moins fiable de la table.

---

## Critère de succès

**|erreur| < 5 % sur tous les cas.** Déjà atteint sur les lignes droites une fois le bonus
de dodge retiré ; l'effort restant porte sur le virage et le flip.

## Pièges de mesure à connaître

- **Rayon d'arrivée.** Le chrono s'arrête à 100 uu de la cible, alors que l'ETA prédit le
  point exact. L'écart est extrapolé avec le modèle d'accélération et affiché entre
  crochets. Sur les trajets courts (< 500 uu) cette correction pèse lourd : ces mesures
  sont les moins fiables.
- **Courses avortées.** Chaque `state set` — y compris un kickoff ou une reprise après but
  — relance le banc. Une ligne `DEPART` sans `ARRIVE` correspondante est une mesure
  interrompue : à ignorer. Ne retenir que les `dist0` qui correspondent au scénario lancé.
- **Étiquettes du script.** Vérifier que le libellé d'un scénario correspond bien à sa
  distance réelle avant de reporter la ligne dans le tableau — une erreur d'étiquette
  corrompt la table sans laisser de trace.
