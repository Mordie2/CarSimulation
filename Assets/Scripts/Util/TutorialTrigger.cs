using UnityEngine;

public class TutorialTrigger : MonoBehaviour
{
    public string playerTag = "Player";
    private TutorialManager tutorial;

    private void Start()
    {
        tutorial = FindObjectOfType<TutorialManager>();

        if (tutorial == null)
        {
            Debug.LogWarning("TutorialManager is not assigned to TutorialTrigger.");
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(playerTag))
        {
            if (tutorial != null)
            {
                tutorial.ShowTutorialStep(1);
            }
            else
            {
                Debug.LogWarning("TutorialManager reference is null. Make sure it is assigned.");
            }
            gameObject.SetActive(false);
        }
    }
}