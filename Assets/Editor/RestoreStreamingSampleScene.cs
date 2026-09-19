#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

/// <summary>
/// Restores the appropriate streaming sample after switching Git branches.
/// Unity can otherwise keep a reference to a scene that does not exist on the
/// newly selected branch and display an empty Temp/__Backupscenes scene.
/// </summary>
[InitializeOnLoad]
internal static class RestoreStreamingSampleScene
{
    private const string SimpleScene =
        "Assets/Jorjin/StreamingSDK/Samples/Scene/StreamingSDKSimpleSample.unity";

    private const string AdvancedScene =
        "Assets/Jorjin/StreamingSDK/Samples/Scene/StreamingSDKAdvancedSample.unity";

    static RestoreStreamingSampleScene()
    {
        EditorApplication.delayCall += RestoreIfNecessary;
    }

    private static void RestoreIfNecessary()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += RestoreIfNecessary;
            return;
        }

        Scene activeScene = SceneManager.GetActiveScene();
        string activePath = (activeScene.path ?? string.Empty).Replace('\\', '/');
        bool hasUsableScene =
            activeScene.IsValid() &&
            !string.IsNullOrEmpty(activePath) &&
            !activePath.StartsWith("Temp/") &&
            File.Exists(ToAbsolutePath(activePath));

        // Follow this branch's configured startup scene, rather than choosing
        // Demo 2 merely because its asset also exists in the project.
        string targetScene = SimpleScene;
        foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
        {
            if (buildScene.enabled && File.Exists(ToAbsolutePath(buildScene.path)))
            {
                targetScene = buildScene.path;
                break;
            }
        }

        EditorSceneManager.playModeStartScene =
            AssetDatabase.LoadAssetAtPath<SceneAsset>(targetScene);

        bool isStreamingSample = activePath == SimpleScene || activePath == AdvancedScene;
        if (hasUsableScene && (!isStreamingSample || activePath == targetScene))
        {
            return;
        }

        // Never discard unsaved work in any open scene during a branch switch.
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneManager.GetSceneAt(i).isDirty)
            {
                return;
            }
        }

        if (File.Exists(ToAbsolutePath(targetScene)))
        {
            EditorSceneManager.OpenScene(targetScene, OpenSceneMode.Single);
        }
    }

    private static string ToAbsolutePath(string assetPath)
    {
        return Path.Combine(Directory.GetCurrentDirectory(), assetPath);
    }
}
#endif
