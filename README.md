# URP-Outline-Render-Feature
Code for creating a render feature in Unity URP that adds outlines to meshes. It uses the Jump Flood Algorithm to do this so it should be pretty fast. However, the code is surely not perfect and could be improved. Besides cleaning things up a bit, you could use Render Layers instead of GameObject layers. I do not believe those were in Unity when I wrote this, or I was just not aware of them.

![Outlines_Moment](https://github.com/FeralPug/URP-Outline-Render-Feature/assets/72169728/a562e617-b870-49ba-8682-6713d9faef3e)

![image_002_0032](https://github.com/FeralPug/URP-Outline-Render-Feature/assets/72169728/436a5e21-23de-4539-8413-d8819a23466f)

https://youtu.be/6uKpv0GHE4Q

To get working follow the steps below

All you should have to do is import or copy the shader and the script into your project and it should work.

Once the script is in your project you should be able to add the render feature to the scriptable render pipeline. This requires that you are using SRP or URP. 

Create the required material using the shader provided and set up the settings for the render feature as you like. The feature only outlines objects that are in the layer that set in the settings struct.

Make sure in project settings > Graphics, Projcet Settings > Quality > Rendering, and on your camera that you have the correct renderer set otherwise it wont work. 

It works best when the set to execute after rendering but feel free to play with the code and the settings to make it work for you.

