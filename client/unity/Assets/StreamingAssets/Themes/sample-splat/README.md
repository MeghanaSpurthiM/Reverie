# sample-splat

`scene.ply` here is a 50,000-splat preview, decimated from the forest theme used in the paper
(2,097,152 splats) by keeping the most visible splats. It exists so the import step can be run
straight from a clone, without downloading the sample themes first:

1. **Reverie → Theme Folder → Import scene.ply as Splat...** and select this folder. It writes
   `scene.asset` and the `scene_*.bytes` files beside `scene.ply` (about 12 MB in total).
2. **Reverie → Theme Folder → Choose...** and select this folder to play it.

It is a preview, not a study asset: it renders sparse and has no panorama, audio or props, so the
3DGS layer is the only one that shows anything. The full themes are in
[`Reverie-SampleThemes.zip`](https://github.com/MeghanaSpurthiM/Reverie/releases/latest), and
`docs/GAME.md` describes the theme format.

The file is stored in Git LFS, so `git lfs install` before cloning or it arrives as a pointer.
