using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed class VHSRendererFeature : ScriptableRendererFeature
{
    #region FEATURE_FIELDS

    private VHSPostRenderPass m_VHSPass;

    #endregion

    #region FEATURE_METHODS

    public override void Create()
    {
        m_VHSPass = new VHSPostRenderPass(name);
    }

    // Override the AddRenderPasses method to inject passes into the renderer. Unity calls AddRenderPasses once per camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_VHSPass == null)
            return;

        // Skip rendering if the target is a Reflection Probe or a preview camera.
        if (renderingData.cameraData.cameraType == CameraType.Preview || renderingData.cameraData.cameraType == CameraType.Reflection)
            return;

        // Skip rendering if the camera is outside the custom volume.
        VHSVolumeComponent myVolume = VolumeManager.instance.stack?.GetComponent<VHSVolumeComponent>();
        if (myVolume == null || !myVolume.IsActive())
            return;

        // Specify when the effect will execute during the frame.
        // For a post-processing effect, the injection point is usually BeforeRenderingTransparents, BeforeRenderingPostProcessing, or AfterRenderingPostProcessing.
        // For more information, refer to https://docs.unity3d.com/Manual/urp/customize/custom-pass-injection-points.html 
        m_VHSPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;

        // Specify that the effect doesn't need scene depth, normals, motion vectors, or the color texture as input.
        m_VHSPass.ConfigureInput(ScriptableRenderPassInput.None);

        // Add the render pass to the renderer.
        renderer.EnqueuePass(m_VHSPass);
    }

    protected override void Dispose(bool disposing)
    {
        
    }

    #endregion

    private class VHSPostRenderPass : ScriptableRenderPass
    {
        #region PASS_FIELDS

        // Declare the material used to render the post-processing effect.
        //private Material m_Material;

        //private static MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();
        //private static MaterialPropertyBlock s_NoisePropertyBlock = new MaterialPropertyBlock();

        // Declare a property that adds or removes depth-stencil support.
        private static readonly bool kBindDepthStencilAttachment = false;

        #endregion
        
        #region resources
        int[] blurPyramid;

        private static Texture2D grainTex
        {
            get
            {
                if (_grainTex == null) _grainTex = (Texture2D)Resources.Load("vhsGrain", typeof(Texture2D));
                return _grainTex;
            }
        }
        private static Texture2D _grainTex;
        private static Texture2D horizontalNoiseTex
        {
            get
            {
                if (_horizontalNoiseTex == null) _horizontalNoiseTex = (Texture2D)Resources.Load("horizontalNoise", typeof(Texture2D));
                return _horizontalNoiseTex;
            }
        }
        private static  Texture2D _horizontalNoiseTex;
        private static Texture2D speckNoiseTex
        {
            get
            {
                if (_speckNoiseTex == null) _speckNoiseTex = (Texture2D)Resources.Load("speckNoise", typeof(Texture2D));
                return _speckNoiseTex;
            }
        }
        private static Texture2D _speckNoiseTex;
        private static Texture2D stripeNoiseTex
        {
            get
            {
                if (_stripeNoiseTex == null) _stripeNoiseTex = (Texture2D)Resources.Load("stripeNoise", typeof(Texture2D));
                return _stripeNoiseTex;
            }
        }
        private static Texture2D _stripeNoiseTex;

        private static Shader shader_downsample
        {
            get
            {
                if (_shader_downsample == null) _shader_downsample = Shader.Find("Hidden/VHSDownsample");
                return _shader_downsample;
            }
        }
        private static Shader _shader_downsample;
        private static Material mat_noiseGen
        {
            get
            {
                if (_mat_noiseGen == null) _mat_noiseGen = new Material(Shader.Find("Hidden/VHSNoiseGen"));
                return _mat_noiseGen;
            }
        }
        private static Material _mat_noiseGen;
        private static Shader shader_smear
        {
            get
            {
                if (_shader_smear == null) _shader_smear = Shader.Find("Hidden/VHSSmear");
                return _shader_smear;
            }
        }
        private static Shader _shader_smear;
        private static Shader shader_composite
        {
            get
            {
                if (_shader_composite == null) _shader_composite = Shader.Find("Hidden/VHSComposite");
                return _shader_composite;
            }
        }
        private static Shader _shader_composite;

        private class VHSState
        {
            public float horizontalNoisePos;
        }
        private static Dictionary<Camera, VHSState> cameraStates = new();

        private static int PyramidID(int i) => Shader.PropertyToID("_VHSPyramid" + i);

        static readonly int noiseBuffer = Shader.PropertyToID("_NoiseBuffer");
        static readonly int smearBuffer = Shader.PropertyToID("_SmearBuffer");
        static readonly int smearBuffer2 = Shader.PropertyToID("_SmearBuffer2");
        #endregion

        public VHSPostRenderPass(string passName)
        {
            // Add a profiling sampler.
            profilingSampler = new ProfilingSampler(passName);

            // To make sure the render pass can sample the active color buffer, set URP to render to intermediate textures instead of directly to the backbuffer.
            requiresIntermediateTexture = true;
        }

        #region PASS_SHARED_RENDERING_CODE

        // Add a command to create the temporary color copy texture.
        // This method is used in both the render graph system path and the Compatibility Mode path.
        private static void ExecuteCopyColorPass(RasterCommandBuffer cmd, RTHandle sourceTexture)
        {
            Blitter.BlitTexture(cmd, sourceTexture, new Vector4(1, 1, 0, 0), 0.0f, false);
        }

        // Get the texture descriptor needed to create the temporary color copy texture.
        // This method is used in both the render graph system path and the Compatibility Mode path.
        private static RenderTextureDescriptor GetCopyPassTextureDescriptor(RenderTextureDescriptor desc)
        {
            // Avoid an unnecessary multisample anti-aliasing (MSAA) resolve before the main render pass.
            desc.msaaSamples = 1;

            // Avoid copying the depth buffer, as the main pass render in this example doesn't use depth.
            desc.depthBufferBits = (int)DepthBits.None;

            return desc;
        }

        #endregion


        #region PASS_RENDER_GRAPH_PATH

        // Declare the resource the copy render pass uses.
        // This method is used only in the render graph system path.
        private class CopyPassData
        {
            public TextureHandle inputTexture;
        }

        private class NoisePassData
        {
            public int noiseTexWidth;
            public int noiseTexHeight;
            public float horizontalNoisePos;
            public float stripeNoiseDensity;
            public Material noiseMat;
        }
        
        private class MainPassData
        {
            public TextureHandle inputTexture;
            public TextureHandle noiseTex;
            public Camera camera;
            public RenderGraph renderGraph;
            public int noiseTexWidth;
            public int noiseTexHeight;
        }

        private static void ExecuteCopyColorPass(CopyPassData data, RasterGraphContext context)
        {
            ExecuteCopyColorPass(context.cmd, data.inputTexture);
        }
        
        private class NoiseGenPassData
        {
            public Camera camera;
        }

        // Override the RecordRenderGraph method to implement the rendering logic.
        // This method is used only in the render graph system path.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            TextureDesc camTexDesc = renderGraph.GetTextureDesc(resourcesData.cameraColor);
            
            VHSVolumeComponent myVolume = VolumeManager.instance.stack?.GetComponent<VHSVolumeComponent>();
            if (myVolume == null) return;

            bool enableNoise = myVolume.stripeNoiseDensity.GetValue<float>() > 0 && myVolume.stripeNoiseOpacity.GetValue<float>() > 0;
            
            if(enableNoise)
            {
                // update state
                if (!cameraStates.TryGetValue(cameraData.camera, out VHSState state))
                {
                    state = new VHSState();
                    cameraStates[cameraData.camera] = state;
                }
                state.horizontalNoisePos += Time.deltaTime * 0.004f;
                if (UnityEngine.Random.value < 0.01f) state.horizontalNoisePos += UnityEngine.Random.value;
                state.horizontalNoisePos = Mathf.Repeat(state.horizontalNoisePos, 1);
                
                int noiseTexHeight = Mathf.Min(480, Mathf.RoundToInt(camTexDesc.height * 0.5f));
                int noiseTexWidth = Mathf.Min(640, Mathf.RoundToInt(camTexDesc.width * 0.5f));
                
                TextureDesc noiseDesc = new TextureDesc(new RenderTextureDescriptor(noiseTexWidth, noiseTexHeight, GraphicsFormat.R8_SNorm, 0));
                TextureHandle noiseTex = renderGraph.CreateTexture(noiseDesc);
                
                using (var builder = renderGraph.AddRasterRenderPass<NoisePassData>(passName, out var passData, profilingSampler))
                {
                    passData.noiseMat = mat_noiseGen;
                    passData.noiseTexWidth = noiseTexWidth;
                    passData.noiseTexHeight = noiseTexHeight;
                    passData.horizontalNoisePos = state.horizontalNoisePos;
                    passData.stripeNoiseDensity = myVolume.stripeNoiseDensity.GetValue<float>();
                    
                    builder.SetRenderAttachment(noiseTex, 0, AccessFlags.Write);
                    
                    builder.AllowPassCulling(false);
                    
                    builder.SetRenderFunc((NoisePassData data, RasterGraphContext context) => ExecuteNoisePass(data, context));
                }
                
                
                //using (var builder = renderGraph.AddRasterRenderPass<CopyPassData>(passName, out var passData, profilingSampler))
                //{
                //    //builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);
//
                //    passData.inputTexture = noiseTex;
                //    builder.SetRenderAttachment(noiseTex, 1, AccessFlags.Read);
                //    
                //    builder.SetRenderFunc((CopyPassData data, RasterGraphContext context) => ExecuteCopyColorPass(data, context));
                //}
            }
            

            /*
            // Sample from the current color texture.
            using (var builder = renderGraph.AddRasterRenderPass<MainPassData>(passName, out var passData, profilingSampler))
            {
                passData.renderGraph = renderGraph;

                TextureHandle destination;
                
                var cameraColorDesc = renderGraph.GetTextureDesc(resourcesData.cameraColor);
                cameraColorDesc.name = "_CameraColorCustomPostProcessing";
                cameraColorDesc.clearBuffer = false;

                destination = renderGraph.CreateTexture(cameraColorDesc);
                passData.inputTexture = resourcesData.cameraColor;

                // If you use framebuffer fetch in your material, use builder.SetInputAttachment to reduce GPU bandwidth usage and power consumption. 
                builder.UseTexture(passData.inputTexture, AccessFlags.Read);
                
                
                // Set the render graph to render to the temporary texture.
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                // Bind the depth-stencil buffer.
                // This is a demonstration. The code isn't used in the example.
                if (kBindDepthStencilAttachment)
                    builder.SetRenderAttachmentDepth(resourcesData.activeDepthTexture, AccessFlags.Write);

                // Set the render method.
                builder.SetRenderFunc((MainPassData data, RasterGraphContext context) => ExecuteMainPass(data, context));

                // Set cameraColor to the new temporary texture so the next render pass can use it. You don't need to blit to and from cameraColor if you use the render graph system.
                resourcesData.cameraColor = destination;
            }*/
            
            
        }

        private static void ExecuteNoisePass(NoisePassData data, RasterGraphContext context)
        {
            mat_noiseGen.SetTexture("_HorizontalNoise", horizontalNoiseTex);
            mat_noiseGen.SetFloat("_HorizontalNoisePos", data.horizontalNoisePos);
            float stripeNoiseDensity = data.stripeNoiseDensity;
            mat_noiseGen.SetFloat("_HorizontalNoisePower", stripeNoiseDensity * stripeNoiseDensity);
            mat_noiseGen.SetTexture("_StripeNoise", stripeNoiseTex);
            mat_noiseGen.SetVector("_StripeNoiseScaleOffset", new Vector4(data.noiseTexWidth / (float)stripeNoiseTex.width, data.noiseTexHeight / (float)stripeNoiseTex.height, UnityEngine.Random.value, UnityEngine.Random.value));

            Blitter.BlitTexture(context.cmd, Texture2D.blackTexture, new Vector4(1,1,0,0), mat_noiseGen, 0);
        }
        
        private static void ExecuteMainPass(MainPassData data, RasterGraphContext context)
        {
            
            
            /*
            // Clear the material properties.
            s_SharedPropertyBlock.Clear();
            if(sourceTexture != null)
                s_SharedPropertyBlock.SetTexture(kBlitTexturePropertyId, sourceTexture);

            // Set the scale and bias so shaders that use Blit.hlsl work correctly.
            s_SharedPropertyBlock.SetVector(kBlitScaleBiasPropertyId, new Vector4(1, 1, 0, 0));

            // Set the material properties based on the blended values of the custom volume.
            // For more information, refer to https://docs.unity3d.com/Manual/urp/post-processing/custom-post-processing-with-volume.html
            VHSVolumeComponent myVolume = VolumeManager.instance.stack?.GetComponent<VHSVolumeComponent>();
            if (myVolume != null)
                s_SharedPropertyBlock.SetFloat("_Intensity", myVolume.intensity.value);

            // Draw to the current render target.
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, s_SharedPropertyBlock);
            */
            
            /*
            

            float blurAmount = Mathf.Clamp(Mathf.Log(context.width * settings.colorBleedRadius * 0.25f, 2f), 3, 8);
            int blurIterations = Mathf.FloorToInt(blurAmount);
            
            // create blur pyramid
            if (blurPyramid == null || blurPyramid.Length != blurIterations) 
            {
                blurPyramid = new int[blurIterations];
                for (int i = 0; i < blurIterations; i++)
                {
                    blurPyramid[i] = PyramidID(i);
                }
            }

            int w = context.width;
            int h = context.height;

            // downsample
            var downsampleSheet = context.propertySheets.Get(shader_downsample);
            downsampleSheet.properties.SetFloat("_BlurBias", settings.colorBleedDirection);
            for (int i = 0; i < blurIterations; i++)
            {
                downsampleSheet.properties.SetVector("_OddScale", GetOddScale(w,h));
                w /= 2;
                h /= 2;
                
                context.GetScreenSpaceTemporaryRT(context.command, blurPyramid[i], 0, context.sourceFormat, RenderTextureReadWrite.Default, FilterMode.Bilinear, w, h);

                if(i == 0)
                {
                    if(enableNoise) context.command.SetGlobalTexture("_Noise", noiseBuffer);
                    downsampleSheet.properties.SetFloat("_NoiseOpacity", settings.stripeNoiseOpacity);
                    context.command.BlitFullscreenTriangle(context.source, blurPyramid[0], downsampleSheet, 0);
                }
                else
                {
                    context.command.BlitFullscreenTriangle(blurPyramid[i - 1], blurPyramid[i], downsampleSheet, 1);
                }
            }

            // upsample
            for (int i = blurIterations - 1; i > 2; i--)
            {
                float fac = 1;
                if (i == blurIterations - 1)
                {
                    fac = blurAmount - blurIterations;
                }
                downsampleSheet.properties.SetFloat("_UpsampleBlend", 0.7f * fac);
                context.command.BlitFullscreenTriangle(blurPyramid[i], blurPyramid[i - 1], downsampleSheet, 2);
            }

            // apply smearing
            bool enableSmearing = settings.smearIntensity >  0;
            if(enableSmearing)
            {
                int sw = Mathf.Min(640, Mathf.RoundToInt(context.width * 0.5f));
                int sh = Mathf.Min(480, Mathf.RoundToInt(context.height * 0.5f));
                context.GetScreenSpaceTemporaryRT(context.command, smearBuffer, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default, FilterMode.Bilinear, sw, sh);
                context.GetScreenSpaceTemporaryRT(context.command, smearBuffer2, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default, FilterMode.Bilinear, sw, sh);
                var smearSheet = context.propertySheets.Get(shader_smear);
                smearSheet.properties.SetVector("_TexelSize", new Vector4(1f/sw, 1f/sh, sw, sh));
                smearSheet.properties.SetVector("_SmearOffsetAttenuation", new Vector4(1, 0.3f));
                context.command.BlitFullscreenTriangle(blurPyramid[1], smearBuffer, smearSheet, 0);
                smearSheet.properties.SetVector("_SmearOffsetAttenuation", new Vector4(5, 1.2f));
                context.command.BlitFullscreenTriangle(smearBuffer, smearBuffer2, smearSheet, 0);
            }

            var compositeSheet = context.propertySheets.Get(shader_composite);
            compositeSheet.properties.SetFloat("_ColorBleedIntensity", settings.colorBleedingIntensity);
            compositeSheet.properties.SetFloat("_BlurIntensity", settings.smearIntensity);
            compositeSheet.properties.SetTexture("_Grain", grainTex);
            compositeSheet.properties.SetFloat("_GrainIntensity", settings.grainIntensity);
            compositeSheet.properties.SetVector("_GrainScaleOffset", new Vector4(0.6f * settings.grainScale, settings.grainScale, UnityEngine.Random.value, UnityEngine.Random.value));
            if(enableNoise) context.command.SetGlobalTexture("_Noise", noiseBuffer);
            compositeSheet.properties.SetFloat("_NoiseOpacity", settings.stripeNoiseOpacity);
            compositeSheet.properties.SetFloat("_EdgeIntensity", settings.edgeIntensity);
            compositeSheet.properties.SetFloat("_EdgeDistance", -settings.edgeDistance);
            context.command.SetGlobalTexture("_VHSSlightBlurredTex", blurPyramid[1]);
            context.command.SetGlobalTexture("_VHSBlurredTex", blurPyramid[2]);
            if(enableSmearing) context.command.SetGlobalTexture("_VHSSmearedTex", smearBuffer2);
            compositeSheet.properties.SetFloat("_SmearIntensity", settings.smearIntensity);
            context.command.BlitFullscreenTriangle(blurPyramid[0], context.destination, compositeSheet, 0);

            // clean up
            for (int i = 0; i < blurPyramid.Length; i++)
            {
                context.command.ReleaseTemporaryRT(blurPyramid[i]);
            }
            if(enableNoise) 
            {
                context.command.ReleaseTemporaryRT(noiseBuffer);
            }
            if(enableSmearing)
            {
                context.command.ReleaseTemporaryRT(smearBuffer);
                context.command.ReleaseTemporaryRT(smearBuffer2);
            }
            */
        }
        
        private static Vector2 GetOddScale(int w, int h)
        {
            bool we = w % 2 == 0;
            bool he = h % 2 == 0;
            int w2 = w / 2;
            int h2 = h / 2;
            return new Vector4(we ? 1 : ((w2 - 1f) / w2),
                he ? 1 : ((h2 - 1f) / h2),
                we ? 1f / w : 1f / (w - 1),
                he ? 1f / h : 1f / (h - 1));
        }

        #endregion
    }
}
