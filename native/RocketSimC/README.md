# RocketSimC — façade C pour appeler RocketSim depuis C#

RocketSim n'a pas de binding C# (Python `mtheall` et Rust `VirxEC` seulement). Cette DLL expose
une API plate `extern "C"` que `RedUtils/Interop/RocketSimNative.cs` appelle en P/Invoke.

## Pourquoi une façade plutôt qu'un marshalling direct

`RocketSim::Vec` est déclaré `RS_ALIGN_16` avec un **4ᵉ float caché** (`_w`) pour que le
compilateur utilise le SIMD : `sizeof(Vec)` vaut **16, pas 12**, et `RotMat` (trois `Vec`) fait
48 octets. Une structure C# calquée sur `CarState` avec des vecteurs de trois floats serait
décalée dès le premier champ — sans erreur de compilation ni d'exécution, juste des résultats
faux. La façade expose donc des structures plates en `float`/`int32` uniquement, dont les deux
côtés sont définis ici.

Même raison pour l'absence de `bool` dans l'API : sa taille de marshalling n'est pas garantie.

## Prérequis

**Cloner RocketSim** — attendu par défaut dans `HardcodeBot/refs/RocketSim`, sinon passer
`-DROCKETSIM_DIR=<chemin>`. C'est le seul prérequis obligatoire.

## Les meshes de collision sont-ils indispensables ? Non.

`Arena::_SetupArenaCollisionShapes` (Arena.cpp:1029+) construit le **sol**, le **plafond** et les
**murs latéraux** en `btStaticPlaneShape`, à partir des constantes — pas de mesh. Les meshes ne
couvrent que les **coins arrondis**, les **rampes** et la **géométrie des buts**.

RocketSim refuse toutefois de démarrer si la liste de meshes est vide (Arena.cpp:997). D'où
`rsc_init_planes_only()` : il fournit via `InitFromMem` un unique triangle valide (56 octets),
placé à z ≈ 2175 uu — au-dessus du plafond à 2048 uu, donc physiquement inatteignable, et sous
`ArenaConfig::maxPos.z` pour rester dans les bornes de la broadphase.

| | |
|---|---|
| **Reste exact** | frappe voiture-balle, rebonds sol / plafond / murs latéraux, balistique |
| **Devient faux, sans aucun signal** | coins arrondis, fond de terrain, entrée dans le but |

Conséquence pratique : juger un tir sur la **vitesse de la balle à la sortie du contact**, et
couper la simulation avant que la balle n'atteigne un coin. Pour savoir si le tir est cadré, tester
soi-même le franchissement du plan de but entre les poteaux — c'est déjà ce que fait
`Ball.Prediction.FindGoal`. Ne jamais laisser la balle courir jusqu'au fond du terrain.

Si un jour tu veux les vrais meshes (tirs dans les coins, jeu sur le backboard), utilise
[RLArenaCollisionDumper](https://github.com/ZealanL/RLArenaCollisionDumper) et appelle `rsc_init`
à la place.

## Construction

Depuis VS Code : tâche **`rocketsim: build`** (voir `vscode-tasks.snippet.json`, à coller dans
`.vscode/tasks.json`). En ligne de commande :

```bat
:: Windows
cd native\RocketSimC
cmake -B build -S . -A x64
cmake --build build --config Release
```

```sh
# macOS / Linux
cd native/RocketSimC
cmake -B build -S . -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

**Pas besoin de copier la bibliothèque à la main** : la cible `CopyRocketSimC` de
`Bot/Bot.csproj` la recopie dans le dossier de sortie à chaque build C#. Construire le natif une
fois suffit.

## Plateformes

| | Construire RocketSimC | Faire tourner le bot en match |
|---|---|---|
| **Windows** | oui (MSVC) | oui |
| **macOS** | oui (Xcode CLT) | **non** — Rocket League n'existe plus sur macOS depuis 2020, et RLBot a besoin du jeu |
| **Linux** | oui | seulement via Proton/Wine, hors périmètre ici |

Sous macOS, la bibliothèque sert donc aux usages **hors-ligne** : bancs, comparaison de modèles
de frappe, mise au point de la recherche. Pour jouer, il faut Windows.

Note GCC/Clang : RocketSim vise MSVC. `Math/MathTypes/MathTypes.h` contient un
`reinterpret_cast` dans un `operator[]` déclaré `constexpr`, que GCC et Clang refusent et que
MSVC accepte — il faut retirer le `constexpr` de ces deux surcharges. La façade elle-même
compile proprement partout.

## Distribution

Voir `THIRD-PARTY.md` : RocketSim est sous MIT et sa notice doit accompagner toute distribution
du bot incluant cette bibliothèque. Sans elle, le bot fonctionne quand même — la recherche se
désactive silencieusement.

## Utilisation

L'unité de travail d'une recherche est le **clone** : on capture l'état courant une fois, puis
on clone l'arène par candidat et on déroule chaque clone indépendamment.

```csharp
RocketSimNative.InitPlanesOnly();                  // une fois par processus (sans meshes)
IntPtr arena = RocketSimNative.ArenaCreate(120f);
uint carId = RocketSimNative.ArenaAddCar(arena, team: 0);

// ... poser l'état courant (voiture + balle), puis par candidat :
IntPtr branch = RocketSimNative.ArenaClone(arena);
RocketSimNative.CarSetControls(branch, carId, in controls);
RocketSimNative.ArenaStep(branch, 60);
RocketSimNative.BallGetState(branch, out var ball);
RocketSimNative.ArenaDestroy(branch);
```

## Avant d'aller plus loin — lire AUDIT §7

Deux obstacles pèsent plus lourd que ce binding :

- **§7.1 le budget de tick** — ~50 candidats sur 0,5 s coûtent ~15 ms, contre 8,33 ms pour un
  tick entier. La recherche doit être **asynchrone**, jamais dans `GetOutput`.
- **§7.2 l'état de départ** — `jumpTime`, `flipTime`, `isFlipping`, `airTimeSinceJump` ne sont
  pas dans le paquet RLBot et doivent être suivis tick par tick. Ce sont exactement les
  compteurs qui décident du déclenchement d'un flip. S'ils sont faux, la recherche optimise une
  voiture qui n'existe pas, et rien ne le signale.

**Garde-fou à mettre en place dès la première utilisation** : simuler 0,3 s en avant depuis
l'état courant, puis comparer à la position réellement atteinte 0,3 s plus tard. Cet écart,
loggé en continu, est la seule chose qui distingue « mauvais plan choisi » de « état de départ
faux ».
