# CustomAssetLoader

Allows the loading of your own custom bundles completely independent from anything in game (unlike Motions)

**What can currently be replaced/added?**

- Gameobjects
- Sprites
- BattleEffectLists
- FMod banks
- Shaders (immersive plagiarism reference)

**How do I utilize this in my mod?**

All bundles must reside within a mod's `custom_bundles` folder.

Alongside this, each bundle must come with a `asset_manifest.json` file, which can then mark objects and match them to the previously discussed types. (For custom Sounds, use `custom_banks` instead, or just use CSound you MF)

**How do I create a manifest?**

- The example's on the Github, but for convenience, I will go over it:

```json
{
  "overrides": [
    {
      "label": "SD_Personality",
      "resourceId": "10805_Ishmeal_FairyAppearance",
      "bundle": "__data",
      "assetPath": "Assets/Resources_moved/Prefab/SD/Personality/10805_Ishmeal_FairyAppearance.prefab",
      "assetType": "GameObject"
    }
  ]
}
```

- Label: The category of the object (i.e abnormalities, personalities, etc etc)
- resourceId: The specific ID that is sent to the game (for custom IDs, you use "SD_Personality" on the label, then input your resourceId by putting a nonexistant path i.e 9999_SigmaSogmaApperacne)
- bundle: The bundle name within the folder.
- assetPath: The path inside of the bundle that leads to the requested asset.
- assetType: The previous types of GameObject, Sprite, BattleEffectList, etc. Defaults to GameObject.

**How do I find which Label I need???**

Labels and resourceId pairs are used in game, so you can either look at existing mods or enable verbose logging in the config settings.

**How do I add FMod Sound banks?**

1. Build the FMOD project and export the `.bank` files.
2. Put them in the `custom_banks` folder.
3. Make sure the `.strings` is present alongside the bank, then you should be able to use them.

**How do I use shader remapping?**

Fawk you. Use immersive plagiarism because its cooler.

# Custom Appearance Basics

First things first, this guide assumes you have one of Doppel's templates. As such, switch the color space to Gamma. This can be done via navigating to Project Settings -> Player -> Other Settings -> Color Space

Now that you've done that, lets go over what you'll likely see.

![](docs/assets/defaultview.png)

I'll assume you need no introduction to most of the UI elements. If you do, go use Custom Motions, which is much easier and convenient.

Now, let's navigate to the basics. Click the `resources_moved` folder, `prefab`, `SD`, `Abnormality`, and then drag the appearance prefab onto the hierarchy like so:

![](docs/assets/prefabappearance.png)

Great. Now we have the prefab appearance. Here resides all of our VFX, alongside various items like spine objects, pivots, blood effects, mang effects, unit scripts, etc. 

I'll briefly cover some of the materials that should be there.

On the prefab, you should find:
- A `CharacterApperacneResiver` script, which is required for the game engine to use your appearance properly. Within it's `appearance` slot, set a reference to your prefab/gameobject that it exists under.
- Depending on the copy of the template, you will find an `AbnormalityAppearnce` script. Every field in there is self explanatory, so I won't bother. Just make sure to fill out every field needed like `Char Info`, `Motion List` to bind timelines and `Sprenderer_charactermotion` to the sprite renderer on your `SpRenderer` object. Said `AbnormalityAppearnce` script can also be swapped for a `CharacterAppearance` script if you want to work with ID appearances; If you do, be sure to also remove the abnormality sprites from the `SpineRenderer` object and add a `CharacterSpineSkin` script in their place.
- A `PlayableDirector`, leave it as is (and replace the playable later once you actually begin work)
- An `Animator`, which you will use when you create a new `Animator Controller` component.
- The `CharacterAppearanceBlood` script, which allows you to edit various properties of blood on the unit.
- The `AbnormalityParts` script, which allows for management of differing phases and the like on an abnormality.
- The `CharacterAppearanceMangController` script, which has a list of `Mang Controllers`. Every Mang VFX needs to have a new mang controller entry that is registered here. Alongside this, the `skill infos` section also allows for activation of mangs on specific Skill IDs.
- And last but not least, the `CharacterAppearanceUpdateBuffState` script, which allows you to activate effects based on the stack of a buff. Populate the `Active Effect` list with a reference to a VFX prefab in order to activate it.

Within `ScaleAndPositionPivot`, and `PivotForAnim`, you will find:
- `SpinePivot`, which holds a `SpineRenderer`.
- `SpRenderer`, which holds all the blood renders and is the unit's Sprite Renderer.
- `DefaultEffectPivot`, which is where all VFX should be parented to.
- `CharacterCenterPoint`, which is pretty self explanatory.
- `CharacterHeightPoint`, which defines the the position of the coin and skill UI on your appearance.

Depending on the copy of your template, you may also find other anchor objects. Those are not required, but you may look at them if you wish.

Navigate back to the `assets` folder, then go to `art`, `qui'lon`, `Animators`, `Phase1`.

Within this folder, you will find the `Animation Controller`. Rename it to match your project and make sure its set on your animator inside your appearance object. It's state is fine as is for now.

Before doing anything, open up the animation window by pressing `Ctrl + 6`, click on your appearance object in the hierarchy window, then press on the arrow on the top left corner of the window and click `Create New Clip`, this is where all of your sprite animations will be made. *if you are prompted to create a new animator, do NOT do it, the only animator for the character sprites should be on the main appearance object*

![](docs/assets/animationclipmaking.png)

When doing your first animation clip, make sure that when you add the `sprite` property of the sprite renderer that is inside of `SpRenderer` to ensure correct scaling. To actually use your sprites in an animationclip you select them in the assets window, change their type to `Sprite (2d and UI)`, change `Sprite Mode` to Single and now you should be able to simply drag them into the animationclip. *its also recommended to set the pivot point of your sprites to where the ground would be and between the legs for correct positioning on certain in game effects*

Next, navigate back to the `qui'lon` folder, then to `Timelines`, and `Phase1`. In here is all the timelines used on the unit, double click any of the `TimelineAssets`. You will see a screen like this:

![](docs/assets/timelinewindow.png)

First things first, locate the SpineRenderer on the asset and remove it if you're planning to use Sprite based animation instead of Spine.

Once you've killed that pesky SpineRenderer, replace it with an Animation Track by right clicking and selecting Animation Track. Once you've done this, click the prefab that you created earlier in the hierarchy, then, take its `SpRenderer` and drag it into the empty slot on the Animation Track. When it prompts to create an animator, press yes.

Great! We have a basic setup for this. Next, go ahead and drag your newly created Animation Clip onto the Animation Track. Then, double click the Animation Clip to bring up the Animation Preview.

Once you've done that, click the prefab in the hierarchy again, and drag in any sprite. It should create a new `[SpRenderer]: Sprite` asset in the animation, and from there, you can animate as usual, however, you will have no preview.

**NOTE**: When making a new TimelineAsset, it needs to be added to the Bindings section on the appearance prefab's `PlayableDirector`. Every new motion also needs to exist on the prefab's `CharacterAppearance` script within it's `Motions List`. If the motion has a timeline, add it to the `Timeline Assets`. If you are using Spine instead, go to `Motion Objects` and enable the Spine Anim option.

# Hit checkers, visual effects

In order to add VFX, you must first parent the VFX to the prefab's DefaultEffectPivot transform. Once that's done, the VFX parent object must have the script Character Attack Effect. On said script, you must link the `This Particle` field to the particlesystem. Once that's done, navigate back to the TimelineAsset, then, right click the `Effect Activate Timeline Track`'s track -> `Add from Character Attack Effect` -> Your VFX. Scale and position as needed.

For hit checkers, you must simply right click the `Skill Give Timing Track`, and select from the list of adds. Alternatively, copy paste an existing give damage timing from the preset TimelineAssets.

