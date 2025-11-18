# VHSEffectURP
I found myself disappointed with a lot of VHS style post effects in games, they tend to just throw some horizontal chromatic aberration and noise and call it a day. I studied the artifacts of real VHS encoding and created this much more realistic image effect.

This version has been rewritten for the Universal Render Pipeline.

![screenshot](/Assets/Samples/screenshot.png)

The elements of this effect are:
## Color Bleeding
In VHS tapes and many other file formats, color information is stored in lower quality than luminance information. This effect blurs the image, extracts the blurry chrominance, and combines it with the high resolution luminance. It uses the real math used for color compression in VHS tapes.
## Grain
This is a simple noise overlay, but it also applies to the chrominance separately from the luminance, to simulate how the chrominance data would have its own noise.
## Smearing
A slight smearing of colors from left to right.
## Stripe Noise
I don't know what causes it, but a common artifact in VHS tapes is bright white pixels with trails fading off to the right. I generate these on the GPU and merge them early on in the effect.
## Edge Sharpening
A distinct feature of VHS video is dark and light fringes to the left and right of sharp vertical edges. This is implemented using a slightly blurred buffer from the color bleeding step offset to the right.
## CRT Filter
A simple CRT texture overlay.

With all of these working together you get a much more authentic VHS filter!

# How to apply this effect
 * Add the VHSRendererFeature to your URP Renderer asset
 * Add the VHSVolumeComponent to a post processing volume