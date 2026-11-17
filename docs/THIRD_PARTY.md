# Third-Party Credits and Licenses

## Base room in the study

The study's base room was assembled from two Unity Asset Store packs. They are **not** in this repository: the Asset Store license does not allow redistribution. This repository ships a placeholder room; [Base Room](BASE_ROOM.md#the-study-room) explains how to rebuild the study room after getting the packs yourself.

| Pack | Version | Used for | License |
|---|---|---|---|
| [Vintage Retro Room URP](https://assetstore.unity.com/packages/slug/288108) (Asset Store product 288108) | 1.0 | walls, floor and ceiling, sofa, armchair, URP materials | Unity Asset Store EULA; see the pack's page |
| [Interior House Assets \| URP](https://assetstore.unity.com/packages/slug/257122) (Asset Store product 257122) | 1.0 | wood-plank floor material and texture | Unity Asset Store EULA; see the pack's page |

We thank the creators of both packs.

## Models in the game

These models were downloaded from [Sketchfab](https://sketchfab.com). Each theme's `sketchfab.json` records the same details.

| Theme | Role | Model | Author | License |
|---|---|---|---|---|
| forest | tool | [Axe](https://sketchfab.com/3d-models/axe-5038f07f634345f5ac9bfa74fb821c97) | Yury Misiyuk | CC-BY-4.0 |
| forest | target | [Log](https://sketchfab.com/3d-models/log-27624ee05fea49899ba2a63e2f6d1e34) | Arkikon | CC-BY-4.0 |
| beach | tool | [Surfboard](https://sketchfab.com/3d-models/surfboard-58873069d37f4dc18993d99d45189b05) | Scott VanArsdale | CC-BY-4.0 |
| beach | target | [Seashell](https://sketchfab.com/3d-models/seashell-410be9cdc3d84f6a9f139189309b4891) | TraianDumbrava | CC-BY-4.0 |

CC-BY-4.0 means [Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/), which permits redistribution with attribution. The `.glb` files ship exactly as downloaded from Sketchfab, unmodified; the game scales, rotates and positions them when it loads a theme.

## Generated theme content

The panoramas, 3DGS scenes and audio were generated with the tools below, and their use may be subject to those tools' terms.

| Component | License | Used for |
|---|---|---|
| [DreamScene360](https://github.com/ShijieZhou-UCLA/DreamScene360) and [3D Gaussian Splatting](https://github.com/graphdeco-inria/gaussian-splatting) | Non-commercial research and evaluation only | Panorama to 3DGS scene |
| Stable Diffusion 2.1 base | CreativeML Open RAIL++-M | Panorama generation (StitchDiffusion) |
| [AudioLDM 2](https://github.com/haoheliu/AudioLDM2) | See its model card | Ambient audio |
| Llama 3.1 8B | Llama 3.1 Community License | Prompt expansion |

**Reverie's generated themes are for non-commercial research use.**

## Software in the game

None of these is stored in this repository. Unity installs each one from `client/unity/Packages/manifest.json` at the version pinned in `packages-lock.json`.

| Component | License |
|---|---|
| [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) by Aras Pranckevičius | MIT |
| [glTFast](https://github.com/atteneder/glTFast) | Apache 2.0 |
| Meta XR Core SDK | Meta Platforms Technologies SDK License |
| Unity Engine | Unity Terms of Service |
| TextMesh Pro Essential Resources and LiberationSans font (imported from Unity's uGUI package on first open; not in this repository) | Unity Companion License; SIL Open Font License |
