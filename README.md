# Geometry Clash

**English** | [简体中文](README.zh-CN.md)

A neon twin-stick shooter built in Unity 6 + URP, inspired by Geometry Wars: Retro Evolved.
**No art or audio assets whatsoever** — every mesh, material, particle and sound effect is generated
procedurally at runtime.

## Getting started

1. Open the project in Unity 6 (6000.3.27f1 / URP 17).
2. Open `Assets/GeometryClash.unity` and press **Play**.

## Entry point

The scene contains **exactly one object**: `GeoClash`, carrying the `Game` component
(`Assets/Scripts/Game.cs`).

Everything else — camera, spring-mass floor, player ship, HUD canvas, post-processing Volume,
audio source — is created in code inside `Game.Awake()`. That's why **the scene view looks empty**:
those objects only appear in the Hierarchy once you hit Play (`GeoClash/MainCamera`,
`GeoClash/World`, `GeoClash/Hud`, …).

The `Game` component exposes live state in the Inspector (State / Score / Lives / Bombs / Wave /
Multiplier), so you can watch the numbers change while playing.

> Safety net: `Boot.cs` has a `[RuntimeInitializeOnLoadMethod]` that looks for a `Game` instance in
> **any** scene and spawns one if it's missing.

## Controls

| Action | Keyboard & mouse | Gamepad |
| --- | --- | --- |
| Move | WASD / arrow keys | Left stick |
| Aim | Mouse | Right stick |
| Fire | Hold left mouse button | Right trigger / push right stick |
| Dash (brief invulnerability) | Shift | RB |
| Bomb (clear the screen) | Space | LB |
| Pause | Esc | Start |
| Mute | M | — |

## Core systems

- **Twin-stick shooting**: the ship's facing is independent of its movement direction; aim with the
  mouse or the right stick.

- **9 geometric enemy types**, each with its own AI:

  | Enemy | Shape | Behaviour |
  | --- | --- | --- |
  | Grunt | Purple square | Straight-line pursuit |
  | Wanderer | Blue triangle | Drifts and bounces, fires in bursts |
  | Dodger | Green diamond | Circles away; splits into two small diamonds on death |
  | Spinner | Cyan hexagon | Hovers around, fires radial bullet patterns |
  | Chomper | Red triangle | Charges up, then dashes in a straight line |
  | Gravity | Orange pentagon | Slow drift, continuously pulls the player toward it |
  | Weevil | Small yellow triangle | Fast weaving pursuit |
  | Hole | Magenta ring | Strong gravity, lethal on contact, high HP |
  | Boss | Large hexagon | Every 5th wave: spiral bullet patterns + summons Weevils |

- **Wave system**: budget-based random composition; enemies fly in from off-screen in groups;
  HP and enemy count scale with the wave; a Boss every 5 waves.

- **Abilities**: Dash (invulnerable repositioning) and Bomb (area clear + wipes enemy bullets +
  grid shockwave).

- **Spring-mass floor**: the entire arena is a **33,417-particle** mass-spring network
  (237 × 141, cell size 0.35). Bullets, impacts and explosions inject impulses; disturbances
  propagate as waves, reflect off the arena borders and decay with damping. The simulation runs on
  a Burst-compiled `IJobParallelFor`.

- **Player respawn**: a black-hole-style grid collapse, plus shards and a dedicated sound effect.
  The ship reappears after 0.2 s with a 3-second invulnerability ring.

- **Depth point cloud**: a Poisson-disk 3D point field behind the arena that produces parallax as
  the camera moves, giving the flat playfield a sense of volume.

## Code structure

```
Assets/
├── GeometryClash.unity        Scene (contains only the GeoClash object)
├── Resources/                 ← shaders must live here or they get stripped from builds
│   ├── Neon.shader            Additive HDR neon (geometry / particles / bullets / trails)
│   └── GridCell.shader        Spring-grid cell: major lines + minor lines + vertex dots
├── Scripts/
│   ├── Boot.cs                Runtime auto-bootstrap
│   ├── Game.cs                Main controller: waves / scoring / collisions / camera / post-processing
│   ├── Player.cs              Player movement, aiming, firing, dashing, respawn
│   ├── Enemy.cs               All 9 enemy behaviours (dispatched by Kind)
│   ├── Bullet.cs / Geom.cs    Bullets and pickups (pooled)
│   ├── Fx.cs                  Particles + explosions + shockwave rings + screen shake
│   ├── SplitFx.cs             Shatters enemy outlines into drifting fragments on death
│   ├── SpringGrid.cs          Mass-spring grid (Burst + Jobs)
│   ├── DepthField.cs          3D Poisson-disk point cloud behind the arena
│   ├── Sfx.cs                 Fully synthesized sound effects (no audio files)
│   ├── Hud.cs                 uGUI interface + floating score popups
│   ├── Controls.cs            Input wrapper (keyboard/mouse + gamepad)
│   ├── Crosshair.cs           Crosshair cursor (procedurally generated texture)
│   ├── Meshes.cs              Mesh generation (polygon outlines / fills / rings / ship)
│   ├── MatLib.cs              Material cache and shared materials
│   ├── Pool.cs                Object pool
│   ├── Palette.cs             Colour palette (HDR values drive bloom intensity)
│   ├── ExplosionProfile.cs    Explosion parameters
│   └── SuctionProfile.cs      Respawn black-hole parameters
├── Settings/                  URP pipeline assets
└── InputSystem_Actions.inputactions  Input System asset
```

## Where to tune

- **Difficulty / pacing**: `budget` in `Game.StartWave()` (currently `(7+n*5)*15`), the four tables
  `Kinds/Weights/UnlockWave/Cost`, and `minDist` in `SpawnPoint()` (default 15 — enemies never spawn
  within 15 units of the player).
- **Enemy stats**: the `switch (kind)` in `Enemy.Setup()`.
- **Game feel**: `Speed / Accel / DashSpeed` at the top of `Player.cs`, plus fire rate and bullet
  speed in `Fire()`.
- **Bloom strength**: the colour components in `Palette.cs` (values above 1 trigger bloom), and the
  threshold/intensity in `Game.BuildPostFx()`.
- **Arena size**: `Game.AX / AY` (currently **41.3 / 24.5**, i.e. 82.6 × 49). Note that
  `SpringGrid`'s particle count is `(2AX/Cell+1) × (2AY/Cell+1)` with `Cell = 0.35`.
- **Pickup drop rate**: `Game.GeomDropRate` (currently 0.2, so only 20% of drops appear).
- **Magnet range and strength**: `AttractRange` (40, slow attraction from anywhere) /
  `MagnetRadius` (4.2 = 3 major cells, snaps in hard once inside) / `CollectRadius` (2.0, auto-collect)
  in `Geom.cs`.
- **How tightly enemies chase**: `leashNear / leashFar` (13 / 22) in `Enemy.cs` — beyond 13 units
  they get pulled back toward the player, beyond 22 they charge straight in.
- **Multiplier growth**: `Game.GeomMultiplierGain` (currently 0.6), `multGrace` (2.5 s grace period
  after a pickup) and the `dt * 0.06f` decay.
- **Framing**: `targetHalfW / targetHalfH` in `Game.UpdateFraming()` (currently **26.6 / 17.5**).
- **Grid springiness**: `Cell / SpringK / AnchorK / Damping / MaxOffset` at the top of `SpringGrid.cs`.
- **Explosions**: `ExplosionProfile.Default` — white flash / shards / screen shake / grid, each group
  with its own `xxxOn` toggle.
- **Respawn black hole**: `SuctionProfile.Default`.

## License

[MIT](LICENSE) © 2026 张成悟

The project contains no third-party art or audio — every mesh, material, particle and sound is
generated in code at runtime, so the entire codebase can be freely used, modified and redistributed.

This is a **gameplay homage** to Geometry Wars: Retro Evolved. It is not affiliated with its
developer and uses none of its assets.
