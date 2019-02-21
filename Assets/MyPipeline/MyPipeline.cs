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
    CommandBuffer cameraBuffer = new CommandBuffer()
    {
        name = "Render Camera"
    };
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
	bool mainLightExists;
    #endregion

    #region Shadows
    RenderTexture shadowMap;
    int shadowMapSize;
    int shadowTileCount;
    float shadowDistance;
    static int shadowMapId = Shader.PropertyToID("_ShadowMap");
    static int worldToShadowMatricesId = Shader.PropertyToID("_WorldToShadowMatrices");
    static int shadowBiasId = Shader.PropertyToID("_ShadowBias");
    static int shadowDataId = Shader.PropertyToID("_ShadowData");
    static int shadowMapSizeId = Shader.PropertyToID("_ShadowMapSize");
    static int globalShadowDataId = Shader.PropertyToID("_GlobalShadowData");
    static int cascadedShadowMapId = Shader.PropertyToID("_CascadedShadowMap");
    static int worldToShadowCascadeMatricesId = Shader.PropertyToID("_WorldToShadowCascadeMatrices");
    static int cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres");
    Vector4[] shadowData = new Vector4[maxVisibleLights];
    Matrix4x4[] worldToShadowMatrices = new Matrix4x4[maxVisibleLights];
    Matrix4x4[] worldToShadowCascadeMatrices = new Matrix4x4[5];
    const string shadowsHardKeyword = "_SHADOWS_HARD";
    const string shadowsSoftKeyword = "_SHADOWS_SOFT";
    int shadowCascades;
	Vector3 shadowCascadeSplit;
    RenderTexture cascadedShadowMap;
    const string cascadedShadowsHardKeyword = "_CASCADED_SHADOWS_HARD";
	const string cascadedShadowsSoftKeyword = "_CASCADED_SHADOWS_SOFT";
    static int cascadedShadowMapSizeId = Shader.PropertyToID("_CascadedShadowMapSize");
	static int cascadedShadowStrengthId = Shader.PropertyToID("_CascadedShadowStrength");
    Vector4[] cascadeCullingSpheres = new Vector4[4];
    CommandBuffer shadowBuffer = new CommandBuffer()
    {
        name = "Render Shadows"
    };
    #endregion

    public MyPipeline(bool enableDynamic, bool enableInstance, int shadowMapSize, float shadowDistance, int shadowCascades, Vector3 shadowCascadeSplit)
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
        this.shadowDistance = shadowDistance;
        this.shadowCascades = shadowCascades;
        this.shadowCascadeSplit = shadowCascadeSplit;
        if (SystemInfo.usesReversedZBuffer)
        {
            worldToShadowCascadeMatrices[4].m33 = 1f;
		}
    }

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
        cullingParameters.shadowDistance = Mathf.Min(shadowDistance, camera.farClipPlane);
        CullResults.Cull(ref cullingParameters, context, ref cull);
        #endregion

        #region ConfigureLightsAndRenderShadows
        if (cull.visibleLights.Count > 0)
        {
            ConfigureLights();
            if (mainLightExists)
            {
				RenderCascadedShadows(context);
			}
            else
            {
				cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
				cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
			}
            if (shadowTileCount > 0)
            {
                RenderShadows(context);
            }
            else
            {
                cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
				cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
            }
        }
        else
        {
            cameraBuffer.SetGlobalVector(lightIndicesOffsetAndCountID, Vector4.zero);
            cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
            cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
            cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
			cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
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
        if (cascadedShadowMap)
        {
			RenderTexture.ReleaseTemporary(cascadedShadowMap);
			cascadedShadowMap = null;
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

        shadowMap = SetShadowRenderTarget();

        shadowBuffer.BeginSample("Render Shadows");
        shadowBuffer.SetGlobalVector(globalShadowDataId, new Vector4(tileScale, shadowDistance * shadowDistance, 0.0f, 0.0f));
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

        int tileIndex = 0;
        bool hardShadows = false;
		bool softShadows = false;
        // If main light exists(using cascaded shadow map), we should skip the first light
        for (int i = mainLightExists ? 1 : 0; i < Mathf.Min(cull.visibleLights.Count, maxVisibleLights); ++i)
        {
            // Get the view and projection matrixs from the spot light
            Matrix4x4 viewMatrix, projectionMatrix;
            ShadowSplitData splitData;
            bool validShadows = shadowData[i].w > 0.0f;
            if(!validShadows)
            {
                continue;
            }
            if(shadowData[i].z > 0.0f)
            {
                validShadows = cull.ComputeDirectionalShadowMatricesAndCullingPrimitives(i, 0, 1, Vector3.right, (int)tileSize, cull.visibleLights[i].light.shadowNearPlane, out viewMatrix, out projectionMatrix, out splitData);
            }
            else
            {
                validShadows = cull.ComputeSpotShadowMatricesAndCullingPrimitives(i, out viewMatrix, out projectionMatrix, out splitData);
            }
            if(!validShadows)
            {
                shadowData[i].x = 0f;
				continue;
            };
            if (shadowData[i].y <= 0f)
				hardShadows = true;
			else
				softShadows = true;
            
            Vector2 tileOffset = ConfigureShadowTile(tileIndex, split, tileSize);
            shadowData[i].z = tileOffset.x * tileScale;  // This z value is resued.
			shadowData[i].w = tileOffset.y * tileScale;
            
            shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            shadowBuffer.SetGlobalFloat(shadowBiasId, cull.visibleLights[i].light.shadowBias);
            context.ExecuteCommandBuffer(shadowBuffer);
            shadowBuffer.Clear();

            var shadowSettings = new DrawShadowsSettings(cull, i);
            shadowSettings.splitData.cullingSphere = splitData.cullingSphere;
            context.DrawShadows(ref shadowSettings);

            CalculateWorldToShadowMatrix(ref viewMatrix, ref projectionMatrix, out worldToShadowMatrices[i]);
            tileIndex += 1;
        }

        shadowBuffer.DisableScissorRect();


        CoreUtils.SetKeyword(shadowBuffer, shadowsHardKeyword, hardShadows);
		CoreUtils.SetKeyword(shadowBuffer, shadowsSoftKeyword, softShadows);
        
        shadowBuffer.SetGlobalTexture(shadowMapId, shadowMap);
        shadowBuffer.SetGlobalVectorArray(shadowDataId, shadowData);
        shadowBuffer.SetGlobalMatrixArray(worldToShadowMatricesId, worldToShadowMatrices);
        
        float invShadowMapSize = 1.0f / shadowMapSize;
		shadowBuffer.SetGlobalVector(shadowMapSizeId, new Vector4(invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize));

        shadowBuffer.EndSample("Render Shadows");
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();
    }

    void RenderCascadedShadows(ScriptableRenderContext context)
    {
        float tileSize = shadowMapSize / 2.0f;
        cascadedShadowMap = SetShadowRenderTarget();
        shadowBuffer.BeginSample("Render Shadows");
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

        Light shadowLight = cull.visibleLights[0].light;
        shadowBuffer.SetGlobalFloat(shadowBiasId, shadowLight.shadowBias);

        var shadowSettings = new DrawShadowsSettings(cull, 0);
		var tileMatrix = Matrix4x4.identity;
		tileMatrix.m00 = tileMatrix.m11 = 0.5f;

        // If main light exists(using cascaded shadow map), we should skip the first light
        for (int i = 0; i < shadowCascades; ++i)
        {
            // Get the view and projection matrixs from the spot light
            Matrix4x4 viewMatrix, projectionMatrix;
            ShadowSplitData splitData;
            cull.ComputeDirectionalShadowMatricesAndCullingPrimitives(0, i, shadowCascades, shadowCascadeSplit, (int)tileSize, shadowLight.shadowNearPlane, out viewMatrix, out projectionMatrix, out splitData); 
            
            Vector2 tileOffset = ConfigureShadowTile(i, 2, tileSize);
            
            shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            shadowBuffer.SetGlobalFloat(shadowBiasId, shadowLight.shadowBias);
            context.ExecuteCommandBuffer(shadowBuffer);
            shadowBuffer.Clear();

            cascadeCullingSpheres[i] = shadowSettings.splitData.cullingSphere = splitData.cullingSphere;
            cascadeCullingSpheres[i].w *= cascadeCullingSpheres[i].w;  // Range to range2
            context.DrawShadows(ref shadowSettings);

            CalculateWorldToShadowMatrix(ref viewMatrix, ref projectionMatrix, out worldToShadowCascadeMatrices[i]);
            tileMatrix.m03 = tileOffset.x * 0.5f;
			tileMatrix.m13 = tileOffset.y * 0.5f;
			worldToShadowCascadeMatrices[i] = tileMatrix * worldToShadowCascadeMatrices[i];
        }

        shadowBuffer.DisableScissorRect();
        shadowBuffer.SetGlobalTexture(cascadedShadowMapId, cascadedShadowMap);
        shadowBuffer.SetGlobalVectorArray(cascadeCullingSpheresId, cascadeCullingSpheres);
        shadowBuffer.SetGlobalMatrixArray(worldToShadowCascadeMatricesId, worldToShadowCascadeMatrices);
        
		float invShadowMapSize = 1.0f / shadowMapSize;
		shadowBuffer.SetGlobalVector(cascadedShadowMapSizeId, new Vector4(invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize));
		shadowBuffer.SetGlobalFloat(cascadedShadowStrengthId, shadowLight.shadowStrength);
		bool hard = shadowLight.shadows == LightShadows.Hard;
		CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsHardKeyword, hard);
		CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsSoftKeyword, !hard);

        shadowBuffer.EndSample("Render Shadows");
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();
    }
    
    RenderTexture SetShadowRenderTarget()
    {
		RenderTexture texture = RenderTexture.GetTemporary(shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap);
		texture.filterMode = FilterMode.Bilinear;
		texture.wrapMode = TextureWrapMode.Clamp;

		CoreUtils.SetRenderTarget(shadowBuffer, texture, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, ClearFlag.Depth);
		return texture;
	}

    Vector2 ConfigureShadowTile (int tileIndex, int split, float tileSize)
    {
		Vector2 tileOffset;
		tileOffset.x = tileIndex % split;
		tileOffset.y = tileIndex / split;
		var tileViewport = new Rect(tileOffset.x * tileSize, tileOffset.y * tileSize, tileSize, tileSize);
		shadowBuffer.SetViewport(tileViewport);
		shadowBuffer.EnableScissorRect(new Rect(tileViewport.x + 4f, tileViewport.y + 4f, tileSize - 8f, tileSize - 8f));
		return tileOffset;
	}

    void CalculateWorldToShadowMatrix(ref Matrix4x4 viewMatrix, ref Matrix4x4 projectionMatrix, out Matrix4x4 worldToShadowMatrix)
	{
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
		worldToShadowMatrix = scaleOffset * (projectionMatrix * viewMatrix);
	}

    void ConfigureLights()
    {
        mainLightExists = false;
        shadowTileCount = 0;
        for (int i = 0; i < Mathf.Min(cull.visibleLights.Count, maxVisibleLights); i++)
        {
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
                shadow = ConfigureShadow(i, light.light);
                shadow.z = 1.0f;  // Directional Shadow
                if (i == 0 && shadow.x > 0f && shadowCascades > 0)
                {
					mainLightExists = true;
                    shadowTileCount -= 1; // This cascaded shadow map do not use tiles.
				}
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
					float innerCos = Mathf.Cos(Mathf.Atan(((46.0f / 64.0f) * outerTan)));
                    float angleRange = Mathf.Max(innerCos - outerCos, 0.001f);
					attenuation.z = 1f / angleRange;
					attenuation.w = -outerCos * attenuation.z;

                    shadow = ConfigureShadow(i, light.light);
				}
			}
            visibleLightAttenuations[i] = attenuation;
            shadowData[i] = shadow;
        }

        // Remove lights which are out of bounds
        if (mainLightExists || cull.visibleLights.Count > maxVisibleLights)
        {
            // TODO: Wait for Unity to fix this memory alloc.
            int[] lightIndices = cull.GetLightIndexMap();
            if (mainLightExists)
            {
				lightIndices[0] = -1;
			}
            for (int i = maxVisibleLights ; i < cull.visibleLights.Count; ++i)
            {
                lightIndices[i] = -1;  // Unity will skip all lights whose index has been changed to −1
            }
            cull.SetLightIndexMap(lightIndices);
        }
    }
    
    Vector4 ConfigureShadow(int lightIndex, Light shadowLight)
    {
        Vector4 shadow = Vector4.zero;
        Bounds shadowBounds;
        if (shadowLight.shadows != LightShadows.None && cull.GetShadowCasterBounds(lightIndex, out shadowBounds))
        {
            shadowTileCount += 1;
            shadow.x = shadowLight.shadowStrength;
            shadow.y = shadowLight.shadows == LightShadows.Soft ? 1.0f : 0.0f;
            shadow.w = 1.0f;
        }
        return shadow;
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
