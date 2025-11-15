using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[VolumeComponentMenu("Post-processing Custom/VHS")]

[VolumeRequiresRendererFeatures(typeof(VHSRendererFeature))]

[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]

public sealed class VHSVolumeComponent : VolumeComponent, IPostProcessComponent
{
    // Set the name of the volume component in the list in the Volume Profile.
    public VHSVolumeComponent()
    {
        displayName = "VHS";
    }

    [Tooltip("Overall intensity multiplier")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 1f);
    
    [Range(0f, 1f)]
    public FloatParameter colorBleedingIntensity = new (0.5f);
    [Range(0, 1), Tooltip("Color bleed iterations")]
    public ClampedFloatParameter colorBleedRadius = new (0.5f, 0, 1);
    [Range(-1, 1), Tooltip("Color bleed direction")]
    public ClampedFloatParameter colorBleedDirection = new (0.0f, -1, 1);
    [Tooltip("Smear Intensity")]
    public ClampedFloatParameter smearIntensity = new (0.0f, 0, 0.8f);
    public ClampedFloatParameter grainIntensity = new (0.1f, 0, 1);
    public ClampedFloatParameter grainScale = new (0.1f, 0.01f, 2f);
    public ClampedFloatParameter stripeNoiseDensity = new (0.1f, 0, 1);
    public ClampedFloatParameter stripeNoiseOpacity = new (1f, 0, 1);
    public ClampedFloatParameter edgeIntensity = new (0.5f, 0, 2);
    public ClampedFloatParameter edgeDistance = new (0.002f, 0f, 0.005f);

    public bool IsActive()
    {
        return intensity.GetValue<float>() > 0 && (colorBleedingIntensity.GetValue<float>() > 0 || edgeIntensity.GetValue<float>() > 0 || (stripeNoiseDensity.GetValue<float>() > 0 && stripeNoiseOpacity.GetValue<float>() > 0) || grainIntensity.GetValue<float>() > 0);
    }
}
