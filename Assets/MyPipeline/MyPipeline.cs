using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Conditional = System.Diagnostics.ConditionalAttribute;

public class MyPipeline : RenderPipeline
{
    #region CommonStuffs
    private CullResults cull;
    private Material errorMaterial;
    private DrawRendererFlags drawFlags;
    #endregion

    #region Lights
    const int maxVisibleLights = 16;
	
	static int visibleLightColorsId =
		Shader.PropertyToID("_VisibleLightColors");
	static int visibleLightDirectionsOrPositionsId =
		Shader.PropertyToID("_VisibleLightDirectionsOrPositions");
    static int visibleLightAttenuationsId =
		Shader.PropertyToID("_VisibleLightAttenuations");
    static int visibleLightSpotDirectionsId =
		Shader.PropertyToID("_VisibleLightSpotDirections");
    static int lightIndicesOffsetAndCountID =
		Shader.PropertyToID("unity_LightIndicesOffsetAndCount");
	
	Vector4[] visibleLightColors = new Vector4[maxVisibleLights];
	Vector4[] visibleLightDirectionsOrPositions = new Vector4[maxVisibleLights];
    Vector4[] visibleLightAttenuations = new Vector4[maxVisibleLights];
    Vector4[] visibleLightSpotDirections = new Vector4[maxVisibleLights];
    #endregion

    #region Shadows
    RenderTexture shadowMap;
    int shadowMapSize;
    int shadowTileCount;
    static int shadowMapId = Shader.PropertyToID("_ShadowMap");
    static int worldToShadowMatricesId = Shader.PropertyToID("_WorldToShadowMatrices");
    static int shadowBiasId = Shader.PropertyToID("_ShadowBias");
    static int shadowDataId = Shader.PropertyToID("_ShadowData");
    static int shadowMapSizeId = Shader.PropertyToID("_ShadowMapSize");
    const int shadowTileBorderSize = 4;
    Vector4[] shadowData = new Vector4[maxVisibleLights];
    Matrix4x4[] worldToShadowMatrices = new Matrix4x4[maxVisibleLights];

    #endregion

    public MyPipeline(bool enableDynamic, bool enableInstance, int shadowMapSize)
    {
        if (enableDynamic)
        {
            drawFlags |= DrawRendererFlags.EnableDynamicBatching;
        }
        if (enableInstance)
        {
            drawFlags |= DrawRendererFlags.EnableInstancing;
        }
        GraphicsSettings.lightsUseLinearIntensity = true;
        this.shadowMapSize = shadowMapSize;
    }

    CommandBuffer cameraBuffer = new CommandBuffer()
    {
        name = "Render Camera"
    };

    CommandBuffer shadowBuffer = new CommandBuffer()
    {
        name = "Render Shadows"
    };

    public override void Render(ScriptableRenderContext renderContext, Camera[] cameras)
    {
		base.Render(renderContext, cameras);
        foreach (var camera in cameras)
        {
            RenderForEachCamera(renderContext, camera);
        }
	}

    private void RenderForEachCamera(ScriptableRenderContext context, Camera camera)
    {
        
        #region SceneUIBeforeCulling
        // Make the scene view show UI elements. This mush be done before culling.
        #if UNITY_EDITOR
		if (camera.cameraType == CameraType.SceneView) {
			ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
		}
        #endif
        #endregion
        
        #region Culling
        ScriptableCullingParameters cullingParameters;
		if (!CullResults.GetCullingParameters(camera, out cullingParameters))
        {
			return;
		}
        CullResults.Cull(ref cullingParameters, context, ref cull);
        #endregion

        #region ConfigureLightsAndShadows
        if (cull.visibleLights.Count > 0)
        {
            ConfigureLights();
            RenderShadows(context);
        }
        else
        {
            cameraBuffer.SetGlobalVector(lightIndicesOffsetAndCountID, Vector4.zero);
        }
        #endregion

        #region Drawing
         context.SetupCameraProperties(camera);
        CameraClearFlags clearFlags = camera.clearFlags;
        cameraBuffer.ClearRenderTarget((clearFlags & CameraClearFlags.Depth) != 0, (clearFlags & CameraClearFlags.Color) != 0, camera.backgroundColor);
        
        cameraBuffer.BeginSample("Render Camera");
        cameraBuffer.SetGlobalVectorArray(visibleLightColorsId, visibleLightColors);
		cameraBuffer.SetGlobalVectorArray(visibleLightDirectionsOrPositionsId, visibleLightDirectionsOrPositions);
        cameraBuffer.SetGlobalVectorArray(visibleLightAttenuationsId, visibleLightAttenuations);
        cameraBuffer.SetGlobalVectorArray(visibleLightSpotDirectionsId, visibleLightSpotDirections);
        context.ExecuteCommandBuffer(cameraBuffer);
        cameraBuffer.Clear();
        
        var drawSettings = new DrawRendererSettings(camera, new ShaderPassName("SRPDefaultUnlit"))
        {
            flags = drawFlags,
            rendererConfiguration = RendererConfiguration.PerObjectLightIndices8
        };
        var filterSettings = new FilterRenderersSettings(true)
        {
            renderQueueRange = RenderQueueRange.opaque,
        };

        drawSettings.sorting.flags = SortFlags.CommonOpaque;
        context.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);
        context.DrawSkybox(camera);
        filterSettings.renderQueueRange = RenderQueueRange.transparent;
        drawSettings.sorting.flags = SortFlags.CommonTransparent;
        context.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);

        DrawDefualtPipeline(context, camera);
        
        cameraBuffer.EndSample("Render Camera");
        context.ExecuteCommandBuffer(cameraBuffer);
        cameraBuffer.Clear();

        context.Submit();
        #endregion

        #region  Clear
        if (shadowMap)
        {
            RenderTexture.ReleaseTemporary(shadowMap);
            shadowMap = null;
        }
        #endregion
    }

    void RenderShadows(ScriptableRenderContext context)
    {
        int split;
		if (shadowTileCount <= 1)
			split = 1;
		else if (shadowTileCount <= 4)
			split = 2;
		else if (shadowTileCount <= 9)
			split = 3;
		else
			split = 4;
        float tileScale = 1.0f / split;
        float tileSize = shadowMapSize / split;

		Rect tileViewport = new Rect(0f, 0f, tileSize, tileSize);

        shadowMap = RenderTexture.GetTemporary(shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap);
        shadowMap.filterMode = FilterMode.Bilinear;
        shadowMap.wrapMode = TextureWrapMode.Clamp;

        shadowBuffer.BeginSample("Render Shadows");
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

        CoreUtils.SetRenderTarget(shadowBuffer, shadowMap, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, ClearFlag.Depth);

        int tileIndex = 0;
        for (int i = 0 ; i < Mathf.Min(cull.visibleLights.Count, maxVisibleLights); ++i)
        {
            // Get the view and projection matrixs from the spot light
            Matrix4x4 viewMatrix, projectionMatrix;
            ShadowSplitData splitData;
            if(!cull.ComputeSpotShadowMatricesAndCullingPrimitives(i, out viewMatrix, out projectionMatrix, out splitData))
            {
                shadowData[i].x = 0f;
				continue;
            };
            float tileOffsetX = tileIndex  % split;
			float tileOffsetY = tileIndex  / split;
			tileViewport.x = tileOffsetX * tileSize;
			tileViewport.y = tileOffsetY * tileSize;
            
            if (split > 1)
            {
                shadowBuffer.SetViewport(tileViewport);
                // Add a border to each shadowmap, in case of bilinear filter.
                shadowBuffer.EnableScissorRect(new Rect(tileViewport.x + shadowTileBorderSize, tileViewport.y + shadowTileBorderSize, tileSize - shadowTileBorderSize * 2, tileSize - shadowTileBorderSize * 2));
            }
            
            shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            shadowBuffer.SetGlobalFloat(shadowBiasId, cull.visibleLights[i].light.shadowBias);
            context.ExecuteCommandBuffer(shadowBuffer);
            shadowBuffer.Clear();

            var shadowSettings = new DrawShadowsSettings(cull, i);
            context.DrawShadows(ref shadowSettings);

            if (SystemInfo.usesReversedZBuffer)
            {
                projectionMatrix.m20 = -projectionMatrix.m20;
                projectionMatrix.m21 = -projectionMatrix.m21;
                projectionMatrix.m22 = -projectionMatrix.m22;
                projectionMatrix.m23 = -projectionMatrix.m23;
            }
            var scaleOffset = Matrix4x4.identity;
            scaleOffset.m00 = scaleOffset.m11 = scaleOffset.m22 = 0.5f;
            scaleOffset.m03 = scaleOffset.m13 = scaleOffset.m23 = 0.5f;
            Matrix4x4 worldToShadowMatrix = scaleOffset * projectionMatrix * viewMatrix;
            worldToShadowMatrices[i] = worldToShadowMatrix;

            if (split > 1)
            {
                // Adjust tiles
                var tileMatrix = Matrix4x4.identity;
                tileMatrix.m00 = tileMatrix.m11 = tileScale;
                tileMatrix.m03 = tileOffsetX * tileScale;
                tileMatrix.m13 = tileOffsetY * tileScale;
                worldToShadowMatrices[i] = tileMatrix * worldToShadowMatrices[i];
            }

            tileIndex += 1;
        }

        if (split > 1)
        {
            shadowBuffer.DisableScissorRect();
        }
        
        shadowBuffer.SetGlobalTexture(shadowMapId, shadowMap);
        shadowBuffer.SetGlobalVectorArray(shadowDataId, shadowData);
        shadowBuffer.SetGlobalMatrixArray(worldToShadowMatricesId, worldToShadowMatrices);
        
        float invShadowMapSize = 1.0f / shadowMapSize;
		shadowBuffer.SetGlobalVector(shadowMapSizeId, new Vector4(invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize));

        shadowBuffer.EndSample("Render Shadows");
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();
    }

    void ConfigureLights()
    {
        shadowTileCount = 0;
        for (int i = 0; i < cull.visibleLights.Count; i++)
        {
            if (i == maxVisibleLights)
            {
				break;
			}
            VisibleLight light = cull.visibleLights[i];
            visibleLightColors[i] = light.finalColor;
            Vector4 attenuation = Vector4.zero;
            attenuation.w = 1f;  // Avoid the influence on other type of lights except for spot lights.
            Vector4 shadow = Vector4.zero;
            if (light.lightType == LightType.Directional)
            {
				Vector4 v = light.localToWorld.GetColumn(2);
				v.x = -v.x;
				v.y = -v.y;
				v.z = -v.z;
				visibleLightDirectionsOrPositions[i] = v;
			}
			else
            {
				visibleLightDirectionsOrPositions[i] = light.localToWorld.GetColumn(3);
                attenuation.x = 1f / Mathf.Max(light.range * light.range, 0.00001f);
                if (light.lightType == LightType.Spot)
                {
					Vector4 v = light.localToWorld.GetColumn(2);
					v.x = -v.x;
					v.y = -v.y;
					v.z = -v.z;
					visibleLightSpotDirections[i] = v;

                    float outerRad = Mathf.Deg2Rad * 0.5f * light.spotAngle;
					float outerCos = Mathf.Cos(outerRad);
                    float outerTan = Mathf.Tan(outerRad);
					float innerCos = Mathf.Cos(Mathf.Atan(((46f / 64f) * outerTan)));
                    float angleRange = Mathf.Max(innerCos - outerCos, 0.001f);
					attenuation.z = 1f / angleRange;
					attenuation.w = -outerCos * attenuation.z;

                    // Shadows
                    Light shadowLight = light.light;
                    Bounds shadowBounds;
					if (shadowLight.shadows != LightShadows.None && cull.GetShadowCasterBounds(i, out shadowBounds))
                    {
                        shadowTileCount += 1;
						shadow.x = shadowLight.shadowStrength;
                        shadow.y = shadowLight.shadows == LightShadows.Soft ? 1f : 0f;
					}
				}
			}
            visibleLightAttenuations[i] = attenuation;
            shadowData[i] = shadow;
        }

        // Remove lights which are out of bounds
        if (cull.visibleLights.Count > maxVisibleLights)
        {
            // TODO: Wait for Unity to fix this memory alloc.
            int[] lightIndices = cull.GetLightIndexMap();
            for (int i = maxVisibleLights ; i < cull.visibleLights.Count; ++i)
            {
                lightIndices[i] = -1;  // Unity will skip all lights whose index has been changed to −1
            }
            cull.SetLightIndexMap(lightIndices);
        }
    }

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
    void DrawDefualtPipeline(ScriptableRenderContext context, Camera camera)
    {
        if (errorMaterial == null)
        {
			Shader errorShader = Shader.Find("Hidden/InternalErrorShader");
			errorMaterial = new Material(errorShader) {
				hideFlags = HideFlags.HideAndDontSave
			};
		}
        var drawSettings = new DrawRendererSettings(camera, new ShaderPassName("ForwardBase"));
        var filterSettings = new FilterRenderersSettings(true);
        drawSettings.SetShaderPassName(1, new ShaderPassName("PrepassBase"));
		drawSettings.SetShaderPassName(2, new ShaderPassName("Always"));
		drawSettings.SetShaderPassName(3, new ShaderPassName("Vertex"));
		drawSettings.SetShaderPassName(4, new ShaderPassName("VertexLMRGBM"));
		drawSettings.SetShaderPassName(5, new ShaderPassName("VertexLM"));
        drawSettings.SetOverrideMaterial(errorMaterial, 0);
        context.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);
    }

}
