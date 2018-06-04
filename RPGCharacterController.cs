using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MGTA
{
    /// <summary>
    /// A Character Controller intended for use with an RPG or other free-roaming, tile-based game.
    /// Meant for use with my other 'Tile' scripts, to handle "Trigger" events and other such features with proper timing.
    /// The character uses a rigidbody to detect triggers, but Trigger events are not processed until the player has fully entered its tile.
    /// Collision is handled through raycasting, and when moving the Character's collider is moved ahead of time to prevent NPCs from accidentally pathing into it.
    /// 
    /// For best results, make sure you evenly position this character with the TileMap!
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    [RequireComponent(typeof(Rigidbody2D))]
    public class RPGCharacterController : MonoBehaviour
    {

        [Header("Movement Timing (in Seconds)")]
        public float stepTime = .25f;                    //time it takes to move to another tile
        public float stepTimeSprint = .125f;            //time it takes to move to another tile while sprinting
        public float pivotBufferTime = .125f;          //When your character is stationary, prevent motion for this amount of time to allow player to 'pivot' by tapping direction keys.
        private float currentStepTime;                //used in calculations, will either be equal to stepTime or stepTimeSprint

        [Header("Tile Movement")]
        public Vector2 occupiedPosition;                //This is the position the character is set to be moving to. Claimed before motion is complete, to prevent NPCs from walking into space.
        public Vector2 forwardDirection = Vector2.down; //This is the direction that the character is 'facing'. No rotation is used on the GameObject.
        private Vector2 forwardDirectionLastFrame;      //cached memory of the direction we were facing in the previous frame. Used with stepDelayOnTurn.
        private BoxCollider2D occupiedPositionCollider; //The collider used to prevent other characters from entering where you're going.
        RaycastHit2D forwardObstacleHit;                //cached raycasthit that detects obstacles ahead.
        Collider2D triggerVolumeLastFrame;              //cached memory of the trigger we occupied last frame.

        [System.Serializable]
        public class KeyAssign
        {
            public KeyCode upKey = KeyCode.W;
            public KeyCode leftKey = KeyCode.A;
            public KeyCode downKey = KeyCode.S;
            public KeyCode rightKey = KeyCode.D;

            public KeyCode sprintKey = KeyCode.LeftShift;
            public KeyCode interactionKey = KeyCode.Space;

            public KeyAssign()
            {

            }

            public KeyAssign(KeyCode left, KeyCode right, KeyCode up, KeyCode down, KeyCode sprint, KeyCode interaction)
            {
                leftKey = left;
                rightKey = right;
                upKey = up;
                downKey = down;
                sprintKey = sprint;
                interactionKey = interaction;
            }
        }
        [Header("Keys")]
        public KeyAssign inputKeys = new KeyAssign();
        //KeyAssign altKeys = new KeyAssign(KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftShift, KeyCode.Space);


        [SerializeField] List<Vector2> directionInput = new List<Vector2>();   //Ordered List<> containing the movement keys. This control method enforces 'D-pad' like movement.

        [Header("Art")]
        public SpriteRenderer spriteRenderer;
        public Sprite downSprite;
        public Sprite upSprite;
        public Sprite rightSprite;
        public Sprite leftSprite;

        [Header("Dialog Support")]
        public Canvas dialogCanvas;
        public bool disableDuringDialog = true;

        [Header("Debug")]
        [SerializeField]
        bool autoFindSpriteOnChild = true;
        [SerializeField] Collider2D occupiedTriggerVolume;                                         //This is the 2Dtrigger the player resides in.                        
                                                                                                   //[SerializeField] InteractableObjectTile selectedInteractable;                              //filled when character is adjacent to, and facing an InteractableObjectTile
        [SerializeField] RPGTriggerEvent occupiedEventTrigger;                                    //filled when character is occupying a 2DTrigger with a TileEventTrigger component
        [SerializeField] Color claimedTileGizmoColor = Color.red, directionGizmoColor = Color.red; //Editor gizmos showing what space you're claiming, and the direction you're facing.

        //a variety of values used to track states in this controller's behavior.
        //[SerializeField]
        bool isLerping = false, continuousMoveInput = false, inputLoopPrevention = false, interactionQueued = false;
        [SerializeField] float pivotBuffer = 0;

        [Header("Physics Settings (CAUTION: Affects global 2D Physics)")]
        [SerializeField]
        bool forceNoQueriesInsideColliders = true, forceNoRaycastHitTriggers = true;                  //For this controller's physics to work properly, make sure these are set to TRUE.

        #region Initialization Methods
        void Awake()
        {
            occupiedPositionCollider = GetComponent<BoxCollider2D>();
            occupiedPosition = transform.position;
            if (autoFindSpriteOnChild)
            {
                Transform spriteObject = this.gameObject.transform.Find("Sprite");
                if (!spriteObject) Debug.LogWarning("Attempted to locate 'Sprite' child on Player, but it could not be found.");
                spriteRenderer = spriteObject.GetComponentInChildren<SpriteRenderer>();
                if (!spriteRenderer) Debug.LogWarning("Attempted to get spriteRenderer from 'Sprite' child on Player, but it did not have one.");
            }
        }

        // Use this for initialization
        void Start()
        {
            if (forceNoQueriesInsideColliders)
                Physics2D.queriesStartInColliders = false;
            if (forceNoRaycastHitTriggers)
                Physics2D.queriesHitTriggers = false;
            if (Physics2D.queriesStartInColliders)
                Debug.LogWarning("CharacterController2D: Physics2D.queriesStartInColliders is currently TRUE. This could lead to unwanted behavior when inside one-way-platform objects.");
            if (Physics2D.queriesHitTriggers)
                Debug.LogWarning("CharacterController2D: Physics2D.queriesHitTriggers is currently TRUE. This could lead to unwanted behavior, such as players colliding with triggers.");
        }
        #endregion

        // Update is called once per frame
        void Update()
        {

            forwardDirectionLastFrame = forwardDirection;

            if (disableDuringDialog && dialogCanvas.enabled) {
                directionInput.Clear();
                inputLoopPrevention = true;
            }
            else
                GetPlayerInput();

            HandleVisuals();

            if (!isLerping && directionInput.Count > 0)
                forwardDirection = directionInput[directionInput.Count - 1];                          //Update your forwardDirection if there's directionalInput.

            //Check the space in front of the player using the forwardDirection
            forwardObstacleHit = CheckSpaceInDirection(forwardDirection);

            if (!isLerping && directionInput.Count == 0) continuousMoveInput = false;

            if (!continuousMoveInput)
            {
                if (forwardDirection != forwardDirectionLastFrame)
                    pivotBuffer = 0;
                else if (directionInput.Count > 0)
                    pivotBuffer += Time.deltaTime;
            }

            if (!isLerping)
            {
                //Perform Interactions on objects in front of the player, if applicable.
                if (interactionQueued && forwardObstacleHit)
                {
                    HandleInteraction();
                }
                else if (directionInput.Count > 0 && !forwardObstacleHit && pivotBuffer >= pivotBufferTime)  //nothing is in front of the player and movement is not being buffered!
                {
                    continuousMoveInput = true;
                    isLerping = true;
                    //selectedInteractable = null;
                    occupiedPosition = transform.position + (Vector3)forwardDirection;
                }
                //else if (pivotBuffer < pivotBufferTime) Debug.Log("Buffering Turn.");
            }

            //if Lerp is flagged above, move the player object towards the occupiedPosition.
            if (isLerping)
            {
                transform.position = Vector3.MoveTowards(transform.position, occupiedPosition, (1 / currentStepTime) * Time.deltaTime);
                occupiedPositionCollider.offset = (Vector3)occupiedPosition - transform.position;
                if (transform.position == (Vector3)occupiedPosition)     //movement is complete.
                {                                                       //this is the point at which we can consider the character to have fully entered the new tile.
                    HandleTileTriggers();
                    isLerping = false;
                }
            }
        }

        void HandleInteraction()
        {
            //is the obstacle in front of us interactable?
            InteractableObject selectedInteractable = forwardObstacleHit.collider.GetComponent<InteractableObject>();
            if (selectedInteractable)
            {
                selectedInteractable.Interact();
                interactionQueued = false;
            }
        }

        void HandleVisuals()
        {
            if (!spriteRenderer)
            {
                Debug.LogWarning("SpriteRenderer not assigned.");
                return;
            }
            if (forwardDirection == Vector2.down) spriteRenderer.sprite = downSprite;
            else if (forwardDirection == Vector2.up) spriteRenderer.sprite = upSprite;
            else if (forwardDirection == Vector2.right) spriteRenderer.sprite = rightSprite;
            else if (forwardDirection == Vector2.left) spriteRenderer.sprite = leftSprite;
        }

        void GetPlayerInput()
        {
            //inputEnableGuard prevents input on the first frame that the script is re-enabled, helping prevent input loops on dialog.
            if (!inputLoopPrevention) interactionQueued = (Input.GetKeyDown(inputKeys.interactionKey));
            else inputLoopPrevention = false;

            //Using this system, the direction key that was pressed LAST has priority!
            if ((Input.GetKey(inputKeys.leftKey)) && !directionInput.Contains(Vector2.left)) directionInput.Add(Vector2.left);
            if ((Input.GetKey(inputKeys.rightKey)) && !directionInput.Contains(Vector2.right)) directionInput.Add(Vector2.right);
            if ((Input.GetKey(inputKeys.upKey)) && !directionInput.Contains(Vector2.up)) directionInput.Add(Vector2.up);
            if ((Input.GetKey(inputKeys.downKey)) && !directionInput.Contains(Vector2.down)) directionInput.Add(Vector2.down);
            if (!Input.GetKey(inputKeys.leftKey)) directionInput.Remove(Vector2.left);
            if (!Input.GetKey(inputKeys.rightKey)) directionInput.Remove(Vector2.right);
            if (!Input.GetKey(inputKeys.upKey)) directionInput.Remove(Vector2.up);
            if (!Input.GetKey(inputKeys.downKey)) directionInput.Remove(Vector2.down);

            currentStepTime = Input.GetKey(inputKeys.sprintKey) ? stepTimeSprint : stepTime;
        }

        RaycastHit2D CheckSpaceInDirection(Vector2 direction)
        {
            RaycastHit2D hit;
            hit = Physics2D.Raycast(transform.position, direction, 1f);

            return hit;
        }

        void HandleTileTriggers()
        {
            //there's been a change in occupiedTrigger, so we must have just entered what's now the OccupiedTrigger
            if (triggerVolumeLastFrame != occupiedTriggerVolume)
            {
                //if the new OccupiedTriggerVolume isn't null (we actually have entered a new volume, and need to talk to the tileEventTrigger)
                if (occupiedTriggerVolume != null)
                {
                    occupiedEventTrigger = occupiedTriggerVolume.GetComponent<RPGTriggerEvent>();
                    if (occupiedEventTrigger) occupiedEventTrigger.EnterInvoke();
                    else Debug.LogWarning("Entered Tile has a trigger over it, but no TileEventTrigger component. Is this intended?");
                }
                //since we have exited the last trigger volume in favor of the new one, lets call ExitInvoke on it.
                if (triggerVolumeLastFrame != null)
                {
                    RPGTriggerEvent exitedTriggerEvent = triggerVolumeLastFrame.GetComponent<RPGTriggerEvent>();
                    if (exitedTriggerEvent) exitedTriggerEvent.ExitInvoke();
                    else Debug.LogWarning("Exited Tile has a trigger over it, but no TileEventTrigger component. Is this intended?");
                }
            }
            //there's been no change in occupiedTrigger, and we ARE inside a trigger volume.
            else if (occupiedTriggerVolume != null)
            {
                occupiedEventTrigger.StayInvoke();
            }

            //refresh lastTriggerVolume
            triggerVolumeLastFrame = occupiedTriggerVolume;
        }

        private void OnTriggerEnter2D(Collider2D col)
        {
            occupiedTriggerVolume = col;
        }

        private void OnTriggerExit2D(Collider2D col)
        {
            if (occupiedTriggerVolume == col) occupiedTriggerVolume = null;
        }

        private void OnDisable()
        {
            directionInput.Clear();
            inputLoopPrevention = true;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = claimedTileGizmoColor;
            if (Application.isPlaying) Gizmos.DrawCube(occupiedPosition, transform.localScale);
            Gizmos.color = directionGizmoColor;
            if (Application.isPlaying) Gizmos.DrawSphere(transform.position + Vector3.Scale((.5f * (Vector3)forwardDirection), transform.localScale), .25f);
        }
    }
}