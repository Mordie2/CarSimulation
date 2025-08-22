using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class TutorialManager : MonoBehaviour
{
    public GameObject tutorialPanel; 
    public GameObject checkpointPanel;  
    public Button skipButton;           
    public Button completeButton;       
    public Button okButton;             

    private int currentStep = 0;

    void Start()
    {
        if (checkpointPanel != null)
        {
            checkpointPanel.SetActive(false);
        }

        if (skipButton != null)
        {
            skipButton.onClick.AddListener(SkipTutorial);
        }

        if (completeButton != null)
        {
            completeButton.onClick.AddListener(CompleteTutorial);
        }

        if (okButton != null)
        {
            okButton.onClick.AddListener(DismissSkipInteraction);
        }

        ShowTutorialStep(currentStep);
    }

    public void ShowTutorialStep(int stepIndex)
    {
        if (stepIndex == 0)
        {
            tutorialPanel.SetActive(true);
            if (checkpointPanel != null)
            {
                checkpointPanel.SetActive(false);
            }
        }
        else if (stepIndex == 1)
        {
            tutorialPanel.SetActive(false);
            if (checkpointPanel != null)
            {
                checkpointPanel.SetActive(true);
            }
        }
    }

    public void SkipTutorial()
    {
        tutorialPanel.SetActive(false);
        GameManager.instance.SkipTutorial();
    }

    public void CompleteTutorial()
    {
        if (checkpointPanel != null)
        {
            checkpointPanel.SetActive(true);
        }
        //GameManager.instance.CompleteTutorial();
    }

    public void DismissSkipInteraction()
    {
        tutorialPanel.SetActive(false);
    }
}
