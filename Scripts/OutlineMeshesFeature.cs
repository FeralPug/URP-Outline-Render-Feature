using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class OutlineMeshesInLayerFeature : ScriptableRendererFeature
{
    //Profiler Names for Debugging
    const string StencilFillProfilerTag = "StencilFillProfile";
    const string OutlineProfilerTag = "OutlineProfile";
    const string JumpFloodProfilerTag = "JumpFloodProfile";

    //rt ids, make sure you use _MainTex in shader or use this names here explicitly
    private int stencilBufferID = Shader.PropertyToID("_StencilBuffer");
    private int silhouetteBufferID = Shader.PropertyToID("_SilhouetteBuffer");
    private int nearestPointID = Shader.PropertyToID("_NearestPoint");
    private int nearestPointPingPongID = Shader.PropertyToID("_NearestPointPingPong");

    //shader properties, Make sure shader uses these names here
    private int outlineColorID = Shader.PropertyToID("_OutlineColor");
    private int outlineWidthID = Shader.PropertyToID("_OutlineWidth");
    private int stepWidthID = Shader.PropertyToID("_JumpFloodStepWidth");
    private int axisWidthID = Shader.PropertyToID("_JumpFloodAxisWidth");

    //shader pass indices, see shader for details on each pass
    const int SHADER_PASS_INTERIOR_STENCIL = 0;
    const int SHADER_PASS_SILHOUETTE_BUFFER_FILL = 1;
    const int SHADER_PASS_JFA_INIT = 2;
    const int SHADER_PASS_JFA_FLOOD = 3;
    const int SHADER_PASS_JFA_FLOOD_SINGLE_AXIS = 4;
    const int SHADER_PASS_JFA_OUTLINE = 5;
    const int SHADER_PASS_BLIT_TO_TARGET = 6;

    //There are three passes
    //1) Draw stencil (target is camera target)
    //2) draw silhouette (target is temp rt)
    //potentially mutliple blits at this stage
    //3) ping pong jump flood and blit to camera target (target is camera target)

    [System.Serializable]
    public class ObjectOutlineSettings
    {
        public bool isEnabled = true;
        //material passes to passes
        public Material outlineMaterial;
        [ColorUsage(true, true)] public Color outlineColor = Color.white;
        [Range(0f, 100f)] public float outlinePixelWidth = 4f;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        public LayerMask layerToRender;
        public bool useSeperableAxisMethod = true;
    }

    //must be named settings for inspector to show
    public ObjectOutlineSettings settings;

    //our first of three passes
    class FillStencilForOutlinePass : ScriptableRenderPass
    {
        readonly List<ShaderTagId> _shaderTagIds = new List<ShaderTagId>();
        readonly ProfilingSampler _profilingSampler;

        Material material;

        FilteringSettings _filteringSettings;
        RenderStateBlock _renderStateBlock;
        int stencilBuffer;
  
        //I hate these constructors but they work. Feel free to make something less verbose
        public FillStencilForOutlinePass(int stencilBuffer, string profilerTag, LayerMask layerMask, Material material, RenderPassEvent renderPassEvent)
        {
            _profilingSampler = new ProfilingSampler(profilerTag);

            this.renderPassEvent = renderPassEvent;
            this.stencilBuffer = stencilBuffer;

            this.material = material;

            //default Filtering settings does no filtering
            //setting the first arg to null (RenderQueueRange) means that all render queues will be rendered
            //the second arg is our layer mask which tells it to only render objects in the layermask
            _filteringSettings = new FilteringSettings(null, layerMask);

            //shader tags are used to determine which shaders/passes should be drawn
            //YOU HAVE TO PROVIDE THESE
            //a null list will fail and an empty list will draw nothing
            //we set a lot of these so that we cover just about everything, you could be more specific
            _shaderTagIds.Add(new ShaderTagId("SRPDefaultUnlit"));
            _shaderTagIds.Add(new ShaderTagId("UniversalForward"));
            _shaderTagIds.Add(new ShaderTagId("UniversalForwardOnly"));
            _shaderTagIds.Add(new ShaderTagId("LightweightForward"));

            //this is used to override the render state of the GPU for DrawRenderers calls.
            //you can override the blend state for example
            //we do not want to do that so we set RenderStateMask.Nothing to do no overrides
            _renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor stencilRTD = renderingData.cameraData.cameraTargetDescriptor;
            
            //make sure your machine supports the texture format, it will fail if it doesn't
            //I used this because by machine supports this format
            stencilRTD.colorFormat = RenderTextureFormat.ARGB32;
            cmd.GetTemporaryRT(stencilBuffer, stencilRTD);

            //sets target as the stencil buffer
            ConfigureTarget(stencilBuffer);
            //sets the clear 
            ConfigureClear(ClearFlag.ColorStencil, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            //for drawRenderers we need these drawingSettings, so we create them
            SortingCriteria sortingCriteria = SortingCriteria.CommonOpaque;
            DrawingSettings drawingSettings = CreateDrawingSettings(
                _shaderTagIds, ref renderingData, sortingCriteria);
            drawingSettings.overrideMaterial = material;
            drawingSettings.overrideMaterialPassIndex = SHADER_PASS_INTERIOR_STENCIL;


            //this buffer is really only used to use the Profiling scope so it is easy to frame debug
            //kind of hacky but w/e
            CommandBuffer cmd = CommandBufferPool.Get(_profilingSampler.name);

            using (new ProfilingScope(cmd, _profilingSampler))
            {
                context.DrawRenderers(
                    renderingData.cullResults, ref drawingSettings,
                    ref _filteringSettings, ref _renderStateBlock);
            }

            //additionally we do not really need to execute/clear the buffer because it has no commands, so just release
            CommandBufferPool.Release(cmd);
        }
    }

    //second of three passes, similar to the first
    class DrawSilhouetteForOutlinePass : ScriptableRenderPass
    {
        Material material;
        ProfilingSampler profileSampler;

        int silhouetteBufferID;

        List<ShaderTagId> _shaderTagIds = new List<ShaderTagId>();
        FilteringSettings _filteringSettings;
        RenderStateBlock _renderStateBlock;

        public DrawSilhouetteForOutlinePass(int silhouetteBufferID, Material material, string profilerTag, LayerMask layerMask, RenderPassEvent renderPassEvent)
        {
            this.silhouetteBufferID = silhouetteBufferID;
            this.material = material;
            profileSampler = new ProfilingSampler(profilerTag);

            this.renderPassEvent = renderPassEvent;

            //default Filtering settings does no filtering
            //setting the first arg to null (RenderQueueRange) means that all render queues will be rendered
            //the second arg is our layer mask which tells it to only render objects in the layermask
            _filteringSettings = new FilteringSettings(null, layerMask);

            //shader tags are used to determine which shaders/passes should be drawn
            //YOU HAVE TO PROVIDE THESE
            //a null list will fail and an empty list will draw nothing
            //we set a lot of these so that we cover just about everything, you could be more specific
            _shaderTagIds.Add(new ShaderTagId("SRPDefaultUnlit"));
            _shaderTagIds.Add(new ShaderTagId("UniversalForward"));
            _shaderTagIds.Add(new ShaderTagId("UniversalForwardOnly"));
            _shaderTagIds.Add(new ShaderTagId("LightweightForward"));

            //this is used to override the render state of the GPU for DrawRenderers calls.
            //you can override the blend state for example
            //we do not want to do that so we set RenderStateMask.Nothing to do no overrides
            _renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
        // a lot of this is from https://gist.github.com/bgolus/a18c1a3fc9af2d73cc19169a809eb195
            int width = renderingData.cameraData.cameraTargetDescriptor.width;
            int height = renderingData.cameraData.cameraTargetDescriptor.height;

            int msaa = (int)MathF.Max(1, QualitySettings.antiAliasing);

            RenderTextureDescriptor silhouetteRTD = new RenderTextureDescriptor()
            {
                dimension = TextureDimension.Tex2D,
                graphicsFormat = GraphicsFormat.R8_UNorm,

                width = width,
                height = height,

                msaaSamples = msaa,
                depthBufferBits = 0,

                useMipMap = false,
                autoGenerateMips = false
            };

            cmd.GetTemporaryRT(silhouetteBufferID, silhouetteRTD, FilterMode.Point);

            ConfigureTarget(silhouetteBufferID, renderingData.cameraData.renderer.cameraDepthTarget);
            ConfigureClear(ClearFlag.Color, Color.clear);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            //for drawRenderers we need these drawingSettings, so we create them
            SortingCriteria sortingCriteria = SortingCriteria.CommonOpaque;
            DrawingSettings drawingSettings = CreateDrawingSettings(
                _shaderTagIds, ref renderingData, sortingCriteria);
            drawingSettings.overrideMaterial = material;
            drawingSettings.overrideMaterialPassIndex = SHADER_PASS_SILHOUETTE_BUFFER_FILL;


            //this buffer is really only used to use the Profiling scope so it is easy to frame debug
            //kind of hacky but w/e
            CommandBuffer cmd = CommandBufferPool.Get(profileSampler.name);

            using (new ProfilingScope(cmd, profileSampler))
            {
                context.DrawRenderers(
                    renderingData.cullResults, ref drawingSettings,
                    ref _filteringSettings, ref _renderStateBlock);
            }

            //once again you shouldn't have to do these execute and clears because it doesn't really have any commands
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }
    }

    // three of three pass, similar to others
    class JumpFloodForOutlinePass : ScriptableRenderPass
    {
        ObjectOutlineSettings settings;

        Material material;
        ProfilingSampler profileSampler;

        int silhouetteBufferID;
        private int nearestPointID;
        private int nearestPointPingPongID;

        //shader properties
        private int outlineColorID;
        private int outlineWidthID;
        private int stepWidthID;
        private int axisWidthID;


        int stencilBuffer;

        public JumpFloodForOutlinePass(int stencilBuffer, int silhouetteBufferID, int nearestPointID, int nearestPointPingPongID,
                int outlineColorID, int outlineWidthID, int stepWidthID, int axisWidthID,
                ObjectOutlineSettings settings, Material material, string profilerTag)
        {
            this.settings = settings;
            this.renderPassEvent = settings.renderPassEvent;

            this.stencilBuffer = stencilBuffer;

            this.silhouetteBufferID = silhouetteBufferID;
            this.nearestPointID = nearestPointID;
            this.nearestPointPingPongID = nearestPointPingPongID;

            this.outlineColorID = outlineColorID;
            this.outlineWidthID = outlineWidthID;
            this.stepWidthID = stepWidthID;
            this.axisWidthID = axisWidthID;

            this.material = material;
            profileSampler = new ProfilingSampler(profilerTag);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            int width = renderingData.cameraData.cameraTargetDescriptor.width;
            int height = renderingData.cameraData.cameraTargetDescriptor.height;

            RenderTextureDescriptor jfaRTD = new RenderTextureDescriptor()
            {
                dimension = TextureDimension.Tex2D,
                graphicsFormat = GraphicsFormat.R16G16_SNorm,

                width = width,
                height = height,

                msaaSamples = 1,
                depthBufferBits = 0,

                //sRGB = false,

                useMipMap = false,
                autoGenerateMips = false
            };

            cmd.GetTemporaryRT(nearestPointID, jfaRTD, FilterMode.Point);
            cmd.GetTemporaryRT(nearestPointPingPongID, jfaRTD, FilterMode.Point);
        }

        //see https://gist.github.com/bgolus/a18c1a3fc9af2d73cc19169a809eb195 for mode details
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cb = CommandBufferPool.Get("JFA");

            using (new ProfilingScope(cb, profileSampler))
            {
                //humus3D wire trick, keep line 1 pixel wide and fade alpha instead of making line smaller
                //slightly nicer looking and no more expensive
                Color adjustedOutlineColor = settings.outlineColor;
                adjustedOutlineColor.a *= Mathf.Clamp01(settings.outlinePixelWidth);
                cb.SetGlobalColor(outlineColorID, adjustedOutlineColor.linear);
                cb.SetGlobalFloat(outlineWidthID, Mathf.Max(1f, settings.outlinePixelWidth));

                //calculate the number of jump flood passes needed for the current outline width
                //+1.0f to handle half pixel inset of the init pass and antialiasing
                int numMips = Mathf.CeilToInt(Mathf.Log(settings.outlinePixelWidth + 1.0f, 2f));
                int jfaIter = numMips - 1;

                //alan wolfe's separable axis jfa https://www.shadertoy.com/view/Mdy3D3
                if (settings.useSeperableAxisMethod)
                {
                    //jfa init
                    cb.Blit(silhouetteBufferID, nearestPointID, material, SHADER_PASS_JFA_INIT);
                    
                    //jfa flood passes
                    for (int i = jfaIter; i >= 0; i--)
                    {
                        //calculate appropriate jump width for each iteration
                        //+0.5 is just me being cautious to avoid any floating point math rounding errors
                        float stepWidth = Mathf.Pow(2, i) + 0.5f;

                        //the two separable passes, one acis at a time
                        cb.SetGlobalVector(axisWidthID, new Vector2(stepWidth, 0f));
                        cb.Blit(nearestPointID, nearestPointPingPongID, material, SHADER_PASS_JFA_FLOOD_SINGLE_AXIS);
                        cb.SetGlobalVector(axisWidthID, new Vector2(0f, stepWidth));
                        cb.Blit(nearestPointPingPongID, nearestPointID, material, SHADER_PASS_JFA_FLOOD_SINGLE_AXIS);
                    }
                }
                //traditional jfa
                else
                {
                    //choose a starting buffer so we always finish on the same buffer
                    int startBufferID = (jfaIter % 2 == 0) ? nearestPointPingPongID : nearestPointID;

                    //jfa init
                    cb.Blit(silhouetteBufferID, startBufferID, material, SHADER_PASS_JFA_INIT);

                    //jfa flood passes
                    for (int i = jfaIter; i >= 0; i--)
                    {
                        //calculate appropriate jump width for each iter
                        // + 0.5f is just me being cautious to avoid any floating point math rounding errors
                        cb.SetGlobalFloat(stepWidthID, MathF.Pow(2, i) + 0.5f);

                        //ping pong between buffers
                        if (i % 2 == 0)
                        {
                            cb.Blit(nearestPointID, nearestPointPingPongID, material, SHADER_PASS_JFA_FLOOD);
                        }
                        else
                        {
                            cb.Blit(nearestPointPingPongID, nearestPointID, material, SHADER_PASS_JFA_FLOOD);
                        }
                    }
                }

                //jfa decode and outline render
                //cb.Blit(nearestPointID, renderingData.cameraData.renderer.cameraColorTarget, material, SHADER_PASS_JFA_OUTLINE);
                cb.Blit(nearestPointID, stencilBuffer, material, SHADER_PASS_JFA_OUTLINE);
                cb.Blit(stencilBuffer, renderingData.cameraData.renderer.cameraColorTarget, material, SHADER_PASS_BLIT_TO_TARGET);
            }

            //this time the command buffer has commands so we need to execute and clear
            context.ExecuteCommandBuffer(cb);
            cb.Clear();
            CommandBufferPool.Release(cb);
        }

        //release buffers
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(stencilBuffer);
            cmd.ReleaseTemporaryRT(silhouetteBufferID);
            cmd.ReleaseTemporaryRT(nearestPointID);
            cmd.ReleaseTemporaryRT(nearestPointPingPongID);
        }
    }

    FillStencilForOutlinePass fillStencilPass;
    DrawSilhouetteForOutlinePass drawSilhouettePass;
    JumpFloodForOutlinePass jumpFloodPass;

    public override void Create()
    {
        if(settings.outlineMaterial == null || !settings.isEnabled)
        {
            return;
        }

        //construct passes
        //these are awful but they just have to pass a lot of info,
        //the passes could get their own ids but I like them in one place. 
        //could be cleaned up a bit
        fillStencilPass = new FillStencilForOutlinePass(stencilBufferID, StencilFillProfilerTag, settings.layerToRender, settings.outlineMaterial, settings.renderPassEvent);
        drawSilhouettePass = new DrawSilhouetteForOutlinePass(silhouetteBufferID, settings.outlineMaterial, OutlineProfilerTag, settings.layerToRender, settings.renderPassEvent);
        jumpFloodPass = new JumpFloodForOutlinePass(stencilBufferID, silhouetteBufferID, nearestPointID, nearestPointPingPongID, outlineColorID, outlineWidthID,
            stepWidthID, axisWidthID, settings, settings.outlineMaterial, JumpFloodProfilerTag);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if(settings.outlineMaterial == null || !settings.isEnabled)
        {
            return;
        }

        renderer.EnqueuePass(fillStencilPass);
        renderer.EnqueuePass(drawSilhouettePass);
        renderer.EnqueuePass(jumpFloodPass);
    }
}
