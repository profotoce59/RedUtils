# Composants tiers embarqués dans RocketSimC

`RocketSimC.dll` lie **statiquement** RocketSim, qui embarque lui-même Bullet Physics. Distribuer
cette bibliothèque revient donc à redistribuer les deux, et leurs licences s'appliquent.

## RocketSim — MIT

Copyright (c) 2022 ZealanL — https://github.com/ZealanL/RocketSim

La licence MIT exige que **la notice de copyright et le texte de la licence accompagnent toute
copie ou portion substantielle du logiciel**. Le fichier `LICENSE` du dépôt RocketSim doit donc
être joint à toute distribution du bot incluant `RocketSimC.dll`.

## Bullet Physics — zlib

Embarqué dans `RocketSim/libsrc/bullet3-3.24`. Licence zlib : usage libre, sans obligation
d'attribution dans les binaires, mais l'origine du logiciel ne doit pas être travestie.

## En pratique, pour publier le bot

Joindre au dossier du bot :

- `RocketSimC.dll` (ou `libRocketSimC.dylib` / `.so`)
- le `LICENSE` de RocketSim
- ce fichier

Le code source de la façade (`rocketsim_c.h` / `.cpp`) est dans le dépôt : rien n'est opaque, ce
qui compte si un tournoi demande à examiner le binaire natif.

**Note importante** : le bot fonctionne sans la bibliothèque. Si on préfère éviter de distribuer
un binaire natif, il suffit de ne pas l'inclure — `ShotSearchRunner.Available` vaut alors faux et
la stratégie garde son comportement d'origine, sans erreur ni message.
