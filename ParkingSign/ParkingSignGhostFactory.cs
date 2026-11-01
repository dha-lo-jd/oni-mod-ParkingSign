using UnityEngine;

namespace ParkingSign;

/// <summary>Builds the translucent duplicant marker shown at a hanging sign's standing cell.</summary>
internal static class ParkingSignGhostFactory
{
    internal static GameObject Create()
    {
        Sprite vanilla = Assets.GetSprite("ui_duplicant_any_selection");
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        if (vanilla != null && shader != null)
        {
            GameObject root = new GameObject("ParkingSignGhost");
            root.SetActive(false);
            GameObject child = new GameObject("Sprite");
            child.transform.SetParent(root.transform, false);
            SpriteRenderer renderer = child.AddComponent<SpriteRenderer>();
            renderer.sprite = vanilla;
            renderer.material = new Material(shader);
            renderer.color = new Color(0.65f, 1f, 0.65f, 0.5f);
            renderer.sortingOrder = 1000;
            float scale = 1.6f / Mathf.Max(vanilla.bounds.size.y, 0.0001f);
            child.transform.localScale = new Vector3(scale, scale, 1f);
            child.transform.localPosition = new Vector3(0f, vanilla.bounds.extents.y * scale, 0f);
            return root;
        }

        ParkingSignLog.Warn("[ParkingSign] ui_duplicant_any_selection unavailable, using animation ghost.");
        return CreateAnimGhost();
    }

    private static GameObject CreateAnimGhost()
    {
        KAnimFile build = Assets.GetAnim("body_comp_default_kanim");
        KAnimFile anim = Assets.GetAnim("anim_idles_default_kanim");
        if (build == null || anim == null)
        {
            ParkingSignLog.Warn("[ParkingSign] Duplicant animation unavailable, using fallback silhouette.");
            return CreateSpriteGhost();
        }

        GameObject go = new GameObject("ParkingSignGhost");
        go.SetActive(false);
        KBatchedAnimController controller = go.AddComponent<KBatchedAnimController>();
        controller.AnimFiles = new[] { build, anim };
        controller.isMovable = true;
        controller.initialAnim = "idle_default";
        controller.initialMode = KAnim.PlayMode.Loop;
        controller.sceneLayer = Grid.SceneLayer.FXFront2;
        controller.visibilityType = KAnimControllerBase.VisibilityType.Always;
        controller.TintColour = new Color32(0, 0, 0, 150);
        return go;
    }

    private static GameObject CreateSpriteGhost()
    {
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        if (shader == null)
        {
            ParkingSignLog.Warn("[ParkingSign] no shader available for the Duplicant silhouette.");
            return null;
        }

        GameObject go = new GameObject("ParkingSignGhost");
        go.SetActive(false);
        SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = GetSilhouetteSprite();
        renderer.material = new Material(shader);
        renderer.color = new Color(0f, 0f, 0f, 0.55f);
        renderer.sortingOrder = 1000;
        go.layer = LayerMask.NameToLayer("Default");
        return go;
    }

    private static Sprite silhouetteSprite;

    private static Sprite GetSilhouetteSprite()
    {
        if (silhouetteSprite != null)
        {
            return silhouetteSprite;
        }

        const int width = 24;
        const int height = 40;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear
        };
        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 solid = new Color32(255, 255, 255, 255);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool head = (x - 12) * (x - 12) + (y - 33) * (y - 33) <= 36;
                bool body = x >= 6 && x < 18 && y >= 14 && y < 27;
                bool arms = (x >= 3 && x < 6 || x >= 18 && x < 21) && y >= 15 && y < 27;
                bool legs = (x >= 7 && x < 11 || x >= 13 && x < 17) && y < 14;
                texture.SetPixel(x, y, head || body || arms || legs ? solid : clear);
            }
        }

        texture.Apply();
        silhouetteSprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0f), 32f);
        return silhouetteSprite;
    }
}
