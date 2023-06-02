# URP-Outline-Render-Feature
Code for creating a render feature in Unity URP that adds outlines to meshes. It uses the Jump Flood Algorythm to do this so it should be pretty fast. However, the code is surely not perfect and could be improved. Besides cleaning things up a bit, you could use Render Layers instead of GameObject layers. I do not believe those were in Unity when I wrote this, or I was just not aware of them.

To get working follow the steps below

All you should have to do is import or copy the shader and the script into your project and it should work.

Once the script is in your project you should be able to add the render feature to the scriptable render pipeline. This requires that you are using SRP or URP. 

Create the required material using the shader provided and set up the settings for the render feature as you like. The feature only outlines objects that are in the layer that set in the settings struct.
