using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;

    public string tutorialSceneName = "TutorialScene";
    public string menuSceneName = "MenuScene";
    public string mainGameSceneName = "MainGameScene";

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        Debug.Log("Active Scene at Start: " + SceneManager.GetActiveScene().name);
        if (SceneManager.GetActiveScene().name == "StartupScene")
        {
            LoadMenu();
        }
    }

    public void LoadTutorial()
    {
        Debug.Log("Loading Tutorial Scene");
        SceneManager.LoadScene(tutorialSceneName);
    }

    public void CompleteTutorial()
    {
        Debug.Log("Completing Tutorial and Starting Game");
        StartGame();
    }

    public void SkipTutorial()
    {
        Debug.Log("Skipping Tutorial and Starting Game");
        StartGame();
    }

    public void LoadMenu()
    {
        Debug.Log("Loading Menu Scene");
        SceneManager.LoadScene(menuSceneName);
    }

    public void StartGame()
    {
        Debug.Log("Starting Main Game Scene");
        SceneManager.LoadScene(mainGameSceneName);
    }

    public void RestartLevel()
    {
        Scene currentScene = SceneManager.GetActiveScene();
        Debug.Log("Restarting Level: " + currentScene.name);
        SceneManager.LoadScene(currentScene.name);
    }

    public void QuitGame()
    {
        Debug.Log("Quitting Game");
        Application.Quit();
    }
}