# HardcodeBot — Référence projet

Bot Rocket League **hardcodé** (décisions déterministes, pas de ML), basé sur la librairie RedUtils en C#. Le bridge Python connecte le bot au framework RLBot via socket.

## Architecture

```
HardcodeBot/
└── RedUtils/               # Repo git principal
    ├── Bot/
    │   ├── Bot.cs          # Stratégie principale (RedBot)
    │   ├── Rotation.cs     # Rôles 2v2 (Attacker / Support)
    │   └── Program.cs      # Entry point C#
    ├── RedUtils/
    │   ├── Bot.cs          # Base framework (RUBot)
    │   ├── Actions/        # 15 actions disponibles
    │   ├── Objects/        # Car, Ball, Field, Game, Goal, Boost…
    │   ├── Math/           # Vec3, Mat3x3
    │   ├── Tools.cs
    │   ├── Utils.cs
    │   └── ExtendedRenderer.cs  # Debug 3D lines/text
    ├── Loadouts/
    │   ├── loadout_generator.py     # Apparence aléatoire parmi les .cfg
    │   └── default_appearance.cfg  # Octane, couleurs blue/orange
    ├── python_run_file.py   # Bridge Python → C# (ExecutableWithSocketAgent)
    └── Bot.cfg              # Config bot (nom, exe, python entry)
```

**Communication** : Python → C# via socket sur le port **36969**.

## Stratégie du bot (RedBot) — état actuel

### Kickoff
- Le plus proche de la balle parmi les coéquipiers fait le kickoff (`Kickoff` — speedflip, non-interruptible)
- L'autre va chercher du boost (`GetBoost`, non-interruptible)

### États de jeu (`Rotation.ComputeGameState`)
Comparaison ETA min de chaque équipe vers la balle (marge 0.3s) :

| État | Condition | Affiché en jeu |
|------|-----------|----------------|
| `Offensive` | Notre ETA < leur ETA − 0.3s | jaune |
| `Contested` | ETAs proches (< 0.3s d'écart) | jaune |
| `Defensive` | Leur ETA < notre ETA − 0.3s | jaune |

### Rotation 2v2 (`Rotation.cs`)
Chaque tick, si `LivingTeammates.Count == 1` :
- `ComputeRole(me, teammate, theirGoal)` calcule un score pour chaque bot :
  - **ETA** : temps pour atteindre le premier slice de `Ball.Prediction` accessible
  - **Pénalité d'angle** : +2s si l'angle car→ball→theirGoal dépasse 108° (tir en arrière)
- Le bot avec le score le plus bas = **Attacker (Player1)**, l'autre = **Support (Player2)**

### Comportement Player1 (Attacker)

| État | Comportement |
|------|-------------|
| `Defensive` | `Drive` vers `Ball.Location` — ferme le gap, transite vers Contested naturellement |
| `Contested` | `FindShot(TheirGoal)` → shot ou `Drive(Ball)` |
| `Offensive` + adversaire a touché en dernier | `FindShot(OurGoal, shootAwayFromGoal: true)` → dégagement |
| `Offensive` + nous avons touché en dernier | `FindShot(TheirGoal)` → tir au but |

`FindShot` parcourt les 360 slices de prédiction, teste dans l'ordre : Aerial → Ground → Jump → DoubleJump.
`Ball.LatestTouch.Team` détermine si le tir vient de l'adversaire.

### Comportement Player2 (Support)
- `Me.Boost < 70` → `GetBoost` (collecte du boost)
- `Me.Boost >= 70` → `Drive` vers `BackupPosition` = 1500 unités derrière Player1 dans la direction de `OurGoal`

### Interruption des actions
Les actions se reset si : terminées (`Finished`), ballon touché + action `Interruptible`, ou bot démoli.

## Actions disponibles (RedUtils/Actions/)

| Action | Description |
|--------|-------------|
| `Drive` | Conduite sol/mur, intègre dodge/speedflip/wavedash automatiquement |
| `Kickoff` | Speedflip kickoff non-interruptible |
| `GroundShot` | Tir au sol |
| `AerialShot` | Tir aérien |
| `JumpShot` | Tir avec saut simple |
| `DoubleJumpShot` | Tir avec double saut |
| `QuickShot` | Tir rapide au sol |
| `GetBoost` | Collecte le meilleur gros boost pad disponible |
| `Dodge` | Dodge/flip |
| `HalfFlip` | Demi-flip (demi-tour rapide) |
| `SpeedFlip` | Flip d'accélération |
| `Wavedash` | Wavedash sur mur |
| `Arrive` | Arrivée à une position cible |
| `Shot` | Classe abstraite de base pour les tirs |

## Objets principaux (RedUtils/Objects/)

### Car
- `Location`, `Velocity`, `AngularVelocity`, `Rotation`, `Orientation`
- `Forward`, `Right`, `Up` — vecteurs déduits de l'orientation
- `IsGrounded`, `HasJumped`, `HasDoubleJumped`, `IsDemolished`, `IsSupersonic`
- `Boost`, `Hitbox`, `Team`

### Ball (propriétés statiques)
- `Ball.Location`, `Ball.Velocity`, `Ball.AngularVelocity`
- `Ball.Prediction` — trajectoire prédite (6s, 360 slices à 1/60s)
- `Ball.LatestTouch`
- `Ball.Predict(float time)` — prédiction physique locale

### Field (propriétés statiques)
- `Field.Goals[]` — buts blue (0) et orange (1)
- `Field.Boosts` — tous les boost pads
- `Field.Surfaces` — toutes les surfaces (sol, plafond, murs, coins, goals)
- `Field.DrivableSurfaces` — surfaces utilisables

### RUBot (base)
- `Me`, `Teammates`, `Opponents`, `LivingTeammates`, `LivingOpponents`
- `OurGoal`, `TheirGoal`, `OurScore`, `TheirScore`
- `IsKickoff`, `DeltaTime`, `Controller`, `Action`

## Constantes utiles

```csharp
// Car
Car.MaxSpeed = 2300
Car.BoostAccel = 991.667
Car.BoostConsumption = 33.3
Car.JumpVel = 291.667
Car.BrakeAccel = 3500

// Ball
Ball.MaxSpeed = 6000
Ball.Radius = 93.15

// Field
Field.Length = 10240   // axe Y
Field.Width = 8192     // axe X
Field.Height = 1950    // axe Z
Field.CornerIntersection = 8064
```

## Config (Bot.cfg)

- **Nom** : RedBot
- **Dev** : CodeRed
- **Exe** : `./Bot/bin/Debug/net6.0/Bot.exe`
- **Python entry** : `./python_run_file.py`
- **Loadout** : `./Loadouts/loadout_generator.py`

## Build & Run

```bash
# Compiler le bot C#
cd RedUtils
dotnet build Bot.sln

# Lancer via RLBot (Python)
# Configurer Bot.cfg dans l'interface RLBot
```
