using System.Collections.Generic;
using UnityEngine;

// 3D Car Coin Collector — drive a little car around a 3D arena (outer walls, a ramp up to
// a raised platform, obstacle blocks of varying height) and grab spinning gold coins.
// WASD / arrow keys to drive. Collect them all to win. The whole scene is generated in
// code (GameObject.CreatePrimitive) so it works in standalone & WebGL, and it coexists
// with AutoShot (in-engine screenshots) and Juice (sound / particles / shake).
public class CarCoinGame : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__CarCoinGame");
        go.AddComponent<CarCoinGame>();
        DontDestroyOnLoad(go);
    }

    // refs
    Rigidbody carRb;
    Transform car;
    Transform cam;
    Camera camComp;
    TextMesh hud;
    TextMesh banner;
    readonly List<Coin> coins = new List<Coin>();
    int total, collected;
    bool won;
    public static int triggerHits;   // debug: how many times any coin trigger fired

    // control state
    float steer, throttle;
    bool attract = true;     // auto-drive demo until the player touches a key
    float stuckT;

    // --- debug / observability (toggle with F1) ---
    TextMesh dbg;
    bool showDbg = false;    // F1 toggles the debug overlay
    float curSpeed, nearDist, dSet, dPost;
    Vector3 dFwd;

    // tuning
    const float Accel = 22f;
    const float MaxSpeed = 15f;
    const float TurnSpeed = 135f;

    void Start()
    {
        // The build's empty scene ships a default Main Camera + Directional Light.
        // Remove them so we don't double-light (washout) or capture the wrong camera.
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        BuildEnvironment();
        BuildCamera();
        BuildCar();
        BuildCoins();
        BuildHud();
        Refresh();
    }

    // ---------- materials / primitives ----------
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.06f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        return m;
    }

    GameObject Box(Vector3 pos, Vector3 size, Color c, bool collide = true)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.transform.position = pos;
        g.transform.localScale = size;
        if (!collide) Destroy(g.GetComponent<Collider>());
        g.GetComponent<Renderer>().sharedMaterial = Mat(c);
        return g;
    }

    // ---------- world ----------
    void BuildEnvironment()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.96f, 0.9f);
        sun.intensity = 1.0f;
        sun.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
        sun.shadows = LightShadows.Soft;
        RenderSettings.fog = false;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.34f, 0.39f, 0.46f);

        // ground
        Box(new Vector3(0, -0.5f, 0), new Vector3(84, 1, 84), new Color(0.27f, 0.5f, 0.32f));
        // a few lighter ground patches for depth cues
        for (int i = 0; i < 6; i++)
        {
            float a = i * 60f * Mathf.Deg2Rad;
            Box(new Vector3(Mathf.Cos(a) * 20f, 0.01f, Mathf.Sin(a) * 20f),
                new Vector3(10, 0.02f, 10), new Color(0.33f, 0.56f, 0.37f), false);
        }

        // outer walls
        Color wc = new Color(0.55f, 0.56f, 0.62f);
        Box(new Vector3(0, 2f, 41.5f), new Vector3(84, 4, 1), wc);
        Box(new Vector3(0, 2f, -41.5f), new Vector3(84, 4, 1), wc);
        Box(new Vector3(41.5f, 2f, 0), new Vector3(1, 4, 84), wc);
        Box(new Vector3(-41.5f, 2f, 0), new Vector3(1, 4, 84), wc);

        // raised platform + ramp (the 3D part: drive up and grab coins on top)
        Box(new Vector3(24, 1.5f, 24), new Vector3(18, 3, 18), new Color(0.46f, 0.42f, 0.52f));
        var ramp = Box(new Vector3(8.5f, 1.0f, 24), new Vector3(13, 0.6f, 9), new Color(0.62f, 0.58f, 0.5f));
        ramp.transform.rotation = Quaternion.Euler(0, 0, 13.5f); // slope up toward +X

        // obstacle blocks of different heights to weave through
        Color oc = new Color(0.78f, 0.38f, 0.32f);
        Vector3[] obs = {
            new Vector3(-16, 1.5f, 6), new Vector3(-9, 1f, -13), new Vector3(-22, 2f, -8),
            new Vector3(6, 2.5f, -20), new Vector3(16, 1.5f, -10), new Vector3(-4, 1f, 16)
        };
        float[] oh = { 3f, 2f, 4f, 5f, 3f, 2f };
        for (int i = 0; i < obs.Length; i++)
        {
            var p = obs[i]; p.y = oh[i] * 0.5f;
            Box(p, new Vector3(3, oh[i], 3), oc);
        }
    }

    // ---------- camera ----------
    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.55f, 0.78f, 0.95f);
        camComp.fieldOfView = 60f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
        cam.position = new Vector3(0, 9, -32);
        cam.rotation = Quaternion.Euler(20f, 0, 0);
    }

    // ---------- car ----------
    void BuildCar()
    {
        var root = new GameObject("Car");
        car = root.transform;
        car.position = new Vector3(0, 1.3f, -3);

        carRb = root.AddComponent<Rigidbody>();
        carRb.mass = 80f;
        carRb.useGravity = false;          // hover: avoids box-on-flat-ground edge jamming
        carRb.linearDamping = 0.4f;
        carRb.angularDamping = 4f;
        carRb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        carRb.interpolation = RigidbodyInterpolation.Interpolate;
        carRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        carRb.sleepThreshold = 0f; // never sleep — we drive it by setting velocity every frame
        var bc = root.AddComponent<BoxCollider>();
        bc.size = new Vector3(2.1f, 1.0f, 4.2f);
        bc.center = new Vector3(0, 0.1f, 0);
        // Frictionless: we handle grip/steering in code, so physics friction must not pin the car.
        var pm = new PhysicsMaterial { dynamicFriction = 0f, staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounciness = 0f };
        bc.material = pm;

        var body = Box(Vector3.zero, new Vector3(2.0f, 0.7f, 4.0f), new Color(0.9f, 0.18f, 0.2f), false);
        body.transform.SetParent(car, false);
        body.transform.localPosition = new Vector3(0, 0.15f, 0);
        var cabin = Box(Vector3.zero, new Vector3(1.6f, 0.7f, 1.9f), new Color(0.15f, 0.2f, 0.28f), false);
        cabin.transform.SetParent(car, false);
        cabin.transform.localPosition = new Vector3(0, 0.7f, -0.2f);
        // nose accent
        var nose = Box(Vector3.zero, new Vector3(1.9f, 0.35f, 0.6f), new Color(1f, 0.85f, 0.2f), false);
        nose.transform.SetParent(car, false);
        nose.transform.localPosition = new Vector3(0, 0.1f, 2.0f);

        // wheels (visual only)
        Vector3[] wpos = {
            new Vector3(1.0f, -0.25f, 1.3f), new Vector3(-1.0f, -0.25f, 1.3f),
            new Vector3(1.0f, -0.25f, -1.3f), new Vector3(-1.0f, -0.25f, -1.3f)
        };
        foreach (var wp in wpos)
        {
            var w = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(w.GetComponent<Collider>());
            w.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.08f, 0.08f, 0.09f));
            w.transform.SetParent(car, false);
            w.transform.localPosition = wp;
            w.transform.localRotation = Quaternion.Euler(0, 0, 90f);
            w.transform.localScale = new Vector3(0.7f, 0.25f, 0.7f);
        }
    }

    // ---------- coins ----------
    void AddCoin(Vector3 pos)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(g.GetComponent<Collider>());
        g.name = "Coin";
        g.transform.position = pos;
        g.transform.rotation = Quaternion.Euler(90f, 0, 0);   // stand the disc upright
        g.transform.localScale = new Vector3(1.1f, 0.09f, 1.1f);
        g.GetComponent<Renderer>().sharedMaterial = Mat(new Color(1f, 0.82f, 0.12f), 0.35f, 0.45f);
        var sc = g.AddComponent<SphereCollider>();
        sc.isTrigger = true;
        sc.radius = 3.5f; // generous pickup
        var coin = g.AddComponent<Coin>();
        coin.game = this;
        coin.baseY = pos.y;
        coins.Add(coin);
    }

    void BuildCoins()
    {
        // ground ring
        for (int i = 0; i < 10; i++)
        {
            float a = i * 36f * Mathf.Deg2Rad;
            AddCoin(new Vector3(Mathf.Cos(a) * 15f, 1.3f, Mathf.Sin(a) * 15f - 3f));
        }
        // a couple mid-field
        AddCoin(new Vector3(-12, 1.3f, -2));
        AddCoin(new Vector3(2, 1.3f, 8));
        // more coins spread across the field
        AddCoin(new Vector3(-18, 1.3f, 14));
        AddCoin(new Vector3(14, 1.3f, 16));
        AddCoin(new Vector3(-14, 1.3f, -18));
        AddCoin(new Vector3(18, 1.3f, -16));
        total = coins.Count;
    }

    // ---------- hud ----------
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("Text").AddComponent<TextMesh>();
        t.fontSize = 90;
        t.characterSize = size;
        t.color = c;
        t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false);
        return t;
    }

    void BuildHud()
    {
        hud = MakeText(0.072f, Color.white, TextAnchor.UpperLeft);
        hud.transform.localPosition = new Vector3(-4.5f, 2.55f, 6.5f);
        hud.transform.localRotation = Quaternion.identity;

        banner = MakeText(0.22f, new Color(1f, 0.9f, 0.25f), TextAnchor.MiddleCenter);
        banner.transform.localPosition = new Vector3(0f, 0.2f, 6f);
        banner.transform.localRotation = Quaternion.identity;
        banner.gameObject.SetActive(false);

        dbg = MakeText(0.045f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.transform.localPosition = new Vector3(-4.6f, -2.5f, 6.5f);
        dbg.transform.localRotation = Quaternion.identity;
        dbg.gameObject.SetActive(showDbg);
    }

    void Refresh()
    {
        if (hud != null) hud.text = "COINS  " + collected + " / " + total;
    }

    public void Collect(Coin c)
    {
        if (c == null || c.taken) return;
        c.taken = true;
        coins.Remove(c);
        collected++;
        Juice.Score(c.transform.position);
        Destroy(c.gameObject);
        Refresh();
        if (collected >= total && !won)
        {
            won = true;
            banner.text = "ALL COINS!\nYOU WIN";
            banner.gameObject.SetActive(true);
            Juice.Score(); Juice.Shake(0.4f);
        }
    }

    // ---------- loop ----------
    void Update()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        if (Mathf.Abs(h) > 0.05f || Mathf.Abs(v) > 0.05f) attract = false;

        if (attract && !won)
        {
            Coin tgt = Nearest();
            if (tgt != null)
            {
                Vector3 to = tgt.transform.position - car.position; to.y = 0;
                float ang = Vector3.SignedAngle(car.forward, to.normalized, Vector3.up);
                steer = Mathf.Clamp(ang / 35f, -1f, 1f);
                throttle = 1f;

                // steer around solid stuff in front (ignore coin triggers)
                if (Physics.Raycast(car.position + car.forward * 2.5f + Vector3.up * 0.3f, car.forward, 3.5f, ~0, QueryTriggerInteraction.Ignore))
                    steer = (ang >= 0f) ? -1f : 1f;

                // unstuck: only after being genuinely jammed for a while, reverse + turn briefly
                if (carRb.linearVelocity.magnitude < 1.0f) stuckT += Time.deltaTime; else stuckT = 0f;
                if (stuckT > 1.5f) { throttle = -1f; steer = 1f; if (stuckT > 2.4f) stuckT = 0f; }
            }
            else { steer = 0; throttle = 0; }
        }
        else { steer = h; throttle = v; stuckT = 0f; }

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }
        if (showDbg && dbg != null)
        {
            Coin n = Nearest();
            nearDist = n != null ? (n.transform.position - car.position).magnitude : -1f;
            dbg.text = string.Format(
                "thr {0:0.0} vel {1:0.0}  setV {2:0.0} postV {3:0.0}\nposXZ ({4:0.0},{5:0.0})  nearest {6:0.0}m\nattract {7} hits {8} coins {9}/{10}",
                throttle, carRb != null ? carRb.linearVelocity.magnitude : 0f, dSet, dPost,
                car.position.x, car.position.z, nearDist, attract ? "ON" : "off", triggerHits, collected, total);
        }
    }

    Coin Nearest()
    {
        Coin best = null; float bd = float.MaxValue;
        foreach (var c in coins)
        {
            if (c == null) continue;
            float d = (c.transform.position - car.position).sqrMagnitude;
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    void FixedUpdate()
    {
        if (carRb == null) return;
        float dt = Time.fixedDeltaTime;
        Vector3 vel = carRb.linearVelocity;
        Vector3 fwd = car.forward;

        // curSpeed is OUR authoritative forward speed. Do NOT re-derive it from the rigidbody:
        // the ground contact zeroes the rigidbody's velocity each step, which would stop any
        // accumulation. We integrate speed ourselves and re-apply it as velocity every frame.
        float desired = throttle * (throttle >= 0f ? MaxSpeed : MaxSpeed * 0.5f);
        float rate = (Mathf.Abs(throttle) > 0.05f ? Accel : Accel * 0.8f) * dt;
        curSpeed = Mathf.MoveTowards(curSpeed, desired, rate);

        Vector3 planar = fwd * curSpeed;                       // forward only -> automatic grip
        if (carRb.IsSleeping()) carRb.WakeUp();
        carRb.linearVelocity = new Vector3(planar.x, 0f, planar.z); // hover: no vertical drift
        dFwd = fwd; dSet = planar.magnitude; dPost = carRb.linearVelocity.magnitude;

        float roll = Mathf.Clamp01(Mathf.Abs(curSpeed) / 3f) * Mathf.Sign(curSpeed >= 0f ? 1f : -1f);
        float yaw = steer * TurnSpeed * dt * roll;
        carRb.MoveRotation(carRb.rotation * Quaternion.Euler(0, yaw, 0));
    }

    void LateUpdate()
    {
        if (cam == null || car == null) return;
        Vector3 back = -car.forward; back.y = 0; back.Normalize();
        Vector3 want = car.position + back * 10.5f + Vector3.up * 7.5f;
        cam.position = Vector3.Lerp(cam.position, want, 1f - Mathf.Exp(-6f * Time.deltaTime));
        Quaternion look = Quaternion.LookRotation((car.position + Vector3.up * 0.3f) - cam.position);
        cam.rotation = Quaternion.Slerp(cam.rotation, look, 1f - Mathf.Exp(-8f * Time.deltaTime));
    }
}

public class Coin : MonoBehaviour
{
    public CarCoinGame game;
    public float baseY;
    [System.NonSerialized] public bool taken;
    float phase;

    void Start() { phase = Random.value * 6.28f; }

    void Update()
    {
        transform.Rotate(0, 180f * Time.deltaTime, 0, Space.World);
        var p = transform.position;
        p.y = baseY + Mathf.Sin(Time.time * 2.2f + phase) * 0.2f;
        transform.position = p;
    }

    void OnTriggerEnter(Collider other)
    {
        CarCoinGame.triggerHits++;
        if (taken || game == null) return;
        if (other.GetComponentInParent<Rigidbody>() != null) game.Collect(this);
    }
}
