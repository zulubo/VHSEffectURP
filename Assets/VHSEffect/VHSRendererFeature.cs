using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
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

        private static Material mat_blur
        {
            get
            {
                if (_mat_blur == null) _mat_blur = new Material(Shader.Find("Hidden/VHSBlur"));
                return _mat_blur;
            }
        }
        private static Material _mat_blur;
        private static Material mat_noiseGen
        {
            get
            {
                if (_mat_noiseGen == null) _mat_noiseGen = new Material(Shader.Find("Hidden/VHSNoiseGen"));
                return _mat_noiseGen;
            }
        }
        private static Material _mat_noiseGen;
        private static Material mat_smear
        {
            get
            {
                if (_mat_smear == null) _mat_smear = new Material(Shader.Find("Hidden/VHSSmear"));
                return _mat_smear;
            }
        }
        private static Material _mat_smear;
        private static Material mat_composite
        {
            get
            {
                if (_mat_composite == null) _mat_composite = new Material(Shader.Find("Hidden/VHSComposite"));
                return _mat_composite;
            }
        }
        private static Material _mat_composite;

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
        
        /*private class MainPassData
        {
            public TextureHandle inputTexture;
            public TextureHandle noiseTex;
            public Camera camera;
            public RenderGraph renderGraph;
            public int noiseTexWidth;
            public int noiseTexHeight;
        }*/

        private static void ExecuteCopyColorPass(CopyPassData data, RasterGraphContext context)
        {
            ExecuteCopyColorPass(context.cmd, data.inputTexture);
        }

        private class VHSData : ContextItem
        {
            public TextureHandle noiseTex;
            public TextureHandle slightBlurTex;
            public TextureHandle blurTex;
            public TextureHandle smearTex;

            public override void Reset()
            {
                noiseTex = TextureHandle.nullHandle;
                slightBlurTex = TextureHandle.nullHandle;
                blurTex = TextureHandle.nullHandle;
                smearTex = TextureHandle.nullHandle;
            }
        }
        

        // Override the RecordRenderGraph method to implement the rendering logic.
        // This method is used only in the render graph system path.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            TextureDesc camTexDesc = renderGraph.GetTextureDesc(resourcesData.cameraColor);

            VHSData vhsData = frameData.Create<VHSData>();
            
            VHSVolumeComponent myVolume = VolumeManager.instance.stack?.GetComponent<VHSVolumeComponent>();
            if (myVolume == null) return;

            bool enableNoise = myVolume.stripeNoiseDensity.GetValue<float>() > 0 && myVolume.stripeNoiseOpacity.GetValue<float>() > 0;
            
            int halfWidth = camTexDesc.width / 2;
            int halfHeight = camTexDesc.height / 2;
                
            int lrWidth = Mathf.Min(640, halfWidth);
            int lrHeight = Mathf.Min(480, halfHeight);
            
            if(enableNoise)
            {
                // update noise state
                if (!cameraStates.TryGetValue(cameraData.camera, out VHSState state))
                {
                    state = new VHSState();
                    cameraStates[cameraData.camera] = state;
                }
                state.horizontalNoisePos += Time.deltaTime * 0.004f;
                if (UnityEngine.Random.value < 0.01f) state.horizontalNoisePos += UnityEngine.Random.value;
                state.horizontalNoisePos = Mathf.Repeat(state.horizontalNoisePos, 1);
                
                TextureDesc noiseDesc = new TextureDesc(new RenderTextureDescriptor(lrWidth, lrHeight, GraphicsFormat.R8_SNorm, 0));
                noiseDesc.name = "VHS Noise";
                noiseDesc.clearBuffer = false;
                vhsData.noiseTex = renderGraph.CreateTexture(noiseDesc);
                
                using (var builder = renderGraph.AddRasterRenderPass<NoisePassData>("VHS Noise", out var passData, profilingSampler))
                {
                    passData.noiseTexWidth = lrWidth;
                    passData.noiseTexHeight = lrHeight;
                    passData.horizontalNoisePos = state.horizontalNoisePos;
                    passData.stripeNoiseDensity = myVolume.stripeNoiseDensity.GetValue<float>();
                    
                    builder.SetRenderAttachment(vhsData.noiseTex, 0, AccessFlags.WriteAll);
                    
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((NoisePassData data, RasterGraphContext context) => ExecuteNoisePass(data, context));
                    
                }
            }

            
            using (var builder = renderGraph.AddUnsafePass<BlurPassData>("VHS Blur", out var passData, profilingSampler))
            {
                // blurring
                float blurAmount = Mathf.Clamp(Mathf.Log(camTexDesc.width * myVolume.colorBleedRadius.GetValue<float>() * 0.25f, 2f), 3, 8);
                int blurIterations = Mathf.FloorToInt(blurAmount);

                passData.source = resourcesData.activeColorTexture;
                builder.UseTexture(resourcesData.activeColorTexture);
                
                // create blur pyramid
                int w = camTexDesc.width;
                int h = camTexDesc.height;
                passData.pyramid = new BlurPassData.BlurPyramidLevel[blurIterations];
                
                TextureDesc blurTexDesc = camTexDesc;
                blurTexDesc.msaaSamples = MSAASamples.None;                
                blurTexDesc.clearBuffer = false;

                for (int i = 0; i < blurIterations; i++)
                {
                    passData.pyramid[i].oddScale = GetOddScale(w, h);
                    w /= 2;
                    h /= 2;
                    passData.pyramid[i].width = w;
                    passData.pyramid[i].height = h;
                    
                    blurTexDesc.width = w;
                    blurTexDesc.height = h;
                    blurTexDesc.name = "Blur Pyramid " + i;
                    passData.pyramid[i].texture = renderGraph.CreateTexture(blurTexDesc);
                    builder.UseTexture(passData.pyramid[i].texture, AccessFlags.ReadWrite);

                    passData.pyramid[i].upsampleBlend = 1;
                    if (i == blurIterations - 1) passData.pyramid[i].upsampleBlend = blurAmount - blurIterations; // smoothly increase blur amount when iterations increase
                    passData.pyramid[i].upsampleBlend *= 0.8f;
                }
                
                passData.enableNoise = enableNoise;
                if (enableNoise)
                {
                    passData.noiseTex = vhsData.noiseTex;
                    builder.UseTexture(passData.noiseTex);
                }
                passData.noiseOpacity = enableNoise ? myVolume.stripeNoiseOpacity.GetValue<float>() * myVolume.intensity.GetValue<float>() : 0;
                passData.blurBias = myVolume.colorBleedDirection.GetValue<float>();
                
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((BlurPassData data, UnsafeGraphContext context) => ExecuteBlurPass(data, context));

                vhsData.slightBlurTex = passData.pyramid[0].texture;
                vhsData.blurTex = passData.pyramid[1].texture;
            }
            
            
            using (var builder = renderGraph.AddUnsafePass<SmearPassData>("VHS Smear", out var passData, profilingSampler))
            {
                passData.sourceTex = vhsData.slightBlurTex;
                builder.UseTexture(vhsData.slightBlurTex);
                
                TextureDesc smearTexDesc = camTexDesc;
                smearTexDesc.width = lrWidth;
                smearTexDesc.height = lrHeight;
                smearTexDesc.msaaSamples = MSAASamples.None;                
                smearTexDesc.clearBuffer = false;

                smearTexDesc.name = "Intermediate Smear Tex";
                passData.smearTex1 = renderGraph.CreateTexture(smearTexDesc);
                builder.UseTexture(passData.smearTex1, AccessFlags.ReadWrite);
                
                smearTexDesc.name = "Smear Tex";
                vhsData.smearTex = renderGraph.CreateTexture(smearTexDesc);
                passData.smearTex2 = vhsData.smearTex;
                builder.UseTexture(passData.smearTex2, AccessFlags.Write);

                passData.texelSize = new Vector4(1f / lrWidth, 1f / lrHeight, lrWidth, lrHeight);
                
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((SmearPassData data, UnsafeGraphContext context) => ExecuteSmearPass(data, context));
            }
            
            
            using (var builder = renderGraph.AddUnsafePass<CompositePassData>("VHS Composite", out var passData, profilingSampler))
            {
                TextureHandle finalTexture = renderGraph.CreateTexture(vhsData.slightBlurTex.GetDescriptor(renderGraph));

                passData.slightBlurredTex = vhsData.slightBlurTex;
                builder.UseTexture(vhsData.slightBlurTex);
                
                passData.blurredTex = vhsData.blurTex;
                builder.UseTexture(vhsData.blurTex);
                
                passData.smearIntensity = myVolume.smearIntensity.GetValue<float>() * myVolume.intensity.GetValue<float>();
                passData.enableSmearing = passData.smearIntensity > 0;
                if (passData.enableSmearing)
                {
                    passData.smearedTex = vhsData.smearTex;
                    builder.UseTexture(vhsData.smearTex);
                }
                
                passData.colorBleedIntensity = myVolume.colorBleedingIntensity.GetValue<float>() * myVolume.intensity.GetValue<float>();
                passData.grainIntensity = myVolume.grainIntensity.GetValue<float>() * myVolume.intensity.GetValue<float>();
                passData.grainScale = myVolume.grainScale.GetValue<float>();
                passData.edgeIntensity = myVolume.edgeIntensity.GetValue<float>() * myVolume.intensity.GetValue<float>();
                passData.edgeDistance = myVolume.edgeDistance.GetValue<float>();

                //builder.SetRenderAttachment(finalTexture, 0, AccessFlags.WriteAll);
                builder.UseTexture(finalTexture, AccessFlags.WriteAll);

                passData.targetTex = finalTexture;
                
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((CompositePassData data, UnsafeGraphContext context) => ExecuteCompositePass(data, context));

                resourcesData.cameraColor = finalTexture;
            }
        }
        

        

        private class NoisePassData
        {
            public int noiseTexWidth;
            public int noiseTexHeight;
            public float horizontalNoisePos;
            public float stripeNoiseDensity;
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
        
        private class BlurPassData
        {
            public TextureHandle source;
            public struct BlurPyramidLevel
            {
                public int width;
                public int height;
                public Vector2 oddScale;
                public TextureHandle texture;
                public float upsampleBlend;
            }
            
            public BlurPyramidLevel[] pyramid;
            
            public bool enableNoise;
            public TextureHandle noiseTex;
            public float noiseOpacity;
            public float blurBias;
        }
        
        private static void ExecuteBlurPass(BlurPassData data, UnsafeGraphContext context)
        {
            CommandBuffer unsafeCmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            
            // downsample and blur
            for (int i = 0; i < data.pyramid.Length; i++)
            {
                MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
                propertyBlock.SetVector("_OddScale", data.pyramid[i].oddScale);
                propertyBlock.SetFloat("_BlurBias", data.blurBias);

                if(i == 0)
                {
                    // add noise to first downsample
                    if(data.enableNoise) propertyBlock.SetTexture("_Noise", data.noiseTex);
                    propertyBlock.SetFloat("_NoiseOpacity", data.noiseOpacity);
                    CustomBlit(unsafeCmd, data.source, data.pyramid[i].texture, mat_blur, 0, propertyBlock);
                }
                else
                {
                    CustomBlit(unsafeCmd, data.pyramid[i - 1].texture, data.pyramid[i].texture, mat_blur, 1, propertyBlock);
                }
            }
            
            // upsample
            // leaves first blur pyramid level untouched to use as an only slightly blurred image (for luminance).
            // second blur pyramid level receives the upsampled heavy blur (for chroma)
            for (int i = data.pyramid.Length - 2; i >= 1; i--)
            {
                MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
                propertyBlock.SetFloat("_UpsampleBlend", data.pyramid[i + 1].upsampleBlend);
                CustomBlit(unsafeCmd, data.pyramid[i + 1].texture, data.pyramid[i].texture, mat_blur, 2, propertyBlock);
            }
        }
        
        private class SmearPassData
        {
            public Vector4 texelSize;
            public TextureHandle sourceTex;
            public TextureHandle smearTex1;
            public TextureHandle smearTex2;
        }
        private static void ExecuteSmearPass(SmearPassData data, UnsafeGraphContext context)
        {
            CommandBuffer unsafeCmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            
            MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
            propertyBlock.SetVector("_TexelSize", data.texelSize);
            propertyBlock.SetVector("_SmearOffsetAttenuation", new Vector4(1, 0.3f));
            CustomBlit(unsafeCmd, data.sourceTex, data.smearTex1, mat_smear, 0, propertyBlock);
            
            MaterialPropertyBlock propertyBlock2 = new MaterialPropertyBlock();
            propertyBlock2.SetVector("_TexelSize", data.texelSize);
            propertyBlock2.SetVector("_SmearOffsetAttenuation", new Vector4(5, 1.2f));
            CustomBlit(unsafeCmd, data.smearTex1, data.smearTex2, mat_smear, 0, propertyBlock);
        }
        
        private class CompositePassData
        {
            public TextureHandle targetTex;
            public TextureHandle slightBlurredTex;
            public TextureHandle blurredTex;
            public bool enableSmearing;
            public TextureHandle smearedTex;
            public float colorBleedIntensity;
            public float smearIntensity;
            public float grainIntensity;
            public float grainScale;
            public float edgeIntensity;
            public float edgeDistance;
        }
        private static void ExecuteCompositePass(CompositePassData data, UnsafeGraphContext context)
        {
            CommandBuffer unsafeCmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            
            mat_composite.SetFloat("_ColorBleedIntensity", data.colorBleedIntensity);
            mat_composite.SetTexture("_Grain", grainTex);
            mat_composite.SetFloat("_GrainIntensity", data.grainIntensity);
            mat_composite.SetVector("_GrainScaleOffset", new Vector4(0.6f * data.grainScale, data.grainScale, UnityEngine.Random.value, UnityEngine.Random.value));
            mat_composite.SetFloat("_EdgeIntensity", data.edgeIntensity);
            mat_composite.SetFloat("_EdgeDistance", -data.edgeDistance);
            mat_composite.SetTexture("_BlurredTex", data.blurredTex);
            if(data.enableSmearing) mat_composite.SetTexture("_SmearedTex", data.smearedTex);
            mat_composite.SetFloat("_SmearIntensity", data.smearIntensity);
            unsafeCmd.SetRenderTarget(data.targetTex);
            Blitter.BlitTexture(context.cmd, data.slightBlurredTex, new Vector4(1,1,0,0), mat_composite, 0);
        }

        private static MaterialPropertyBlock customBlitPropertyBlock;
        private static void CustomBlit(CommandBuffer cmd, TextureHandle source, TextureHandle dest, Material material, int pass, MaterialPropertyBlock propertyBlock)
        {
            if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
            propertyBlock.SetVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
            propertyBlock.SetTexture("_BlitTexture", source);
            cmd.SetRenderTarget(dest);
            cmd.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, 3, 1, propertyBlock);
        }
        
        /*
        private class BlurUpsamplePassData
        {
            public float blend;
            public TextureHandle inputTex;
        }
        private static void BlurUpsamplePass(BlurUpsamplePassData data, RasterGraphContext context)
        {
            mat_downsample.SetFloat("_UpsampleBlend", data.blend);
            Blitter.BlitTexture(context.cmd, data.inputTex, new Vector4(1,1,0,0), mat_downsample, 2);
            
        }
        */
        
        
        
            /*
        private static void ExecuteMainPass(MainPassData data, RasterGraphContext context)
        {
            
            
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
        }
            */
        
        private static Vector2 GetOddScale(int w, int h)
        {
            bool widthEven = w % 2 == 0;
            bool heightEven = h % 2 == 0;
            int halfWidth = w / 2;
            int halfHeight = h / 2;
            return new Vector4(widthEven ? 1 : ((halfWidth - 1f) / halfWidth),
                                    heightEven ? 1 : ((halfHeight - 1f) / halfHeight),
                                    widthEven ? 1f / w : 1f / (w - 1),
                                    heightEven ? 1f / h : 1f / (h - 1));
        }

        #endregion
    }
}
