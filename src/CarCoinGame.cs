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
    int stage = 1;
    float winTimer;
    readonly List<GameObject> stageObjects = new List<GameObject>(); // obstacles+coins of current stage
    Material goldMat;                 // shared gold material (coins + collect sparks)
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

        goldMat = Mat(new Color(1f, 0.82f, 0.12f), 0.4f, 0.55f);
        BuildEnvironment();
        BuildCamera();
        BuildCar();
        BuildHud();
        BuildStage();          // obstacles + coins for stage 1
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
        g.GetComponent<Renderer>().sharedMaterial = goldMat;
        var sc = g.AddComponent<SphereCollider>();
        sc.isTrigger = true;
        sc.radius = 3.5f; // generous pickup
        var coin = g.AddComponent<Coin>();
        coin.game = this;
        coin.baseY = pos.y;
        coins.Add(coin);
        stageObjects.Add(g);
    }

    // Build the current stage: fresh random obstacles + coins (counts grow each stage).
    void BuildStage()
    {
        foreach (var o in stageObjects) if (o != null) Destroy(o);
        stageObjects.Clear();
        coins.Clear();

        int nObs = Mathf.Min(3 + stage, 9);
        Color oc = new Color(0.78f, 0.38f, 0.32f);
        for (int i = 0; i < nObs; i++)
        {
            Vector2 p = RandPos(10f, 31f);
            float hgt = Random.Range(2f, 4.5f);
            stageObjects.Add(Box(new Vector3(p.x, hgt * 0.5f, p.y), new Vector3(3f, hgt, 3f), oc));
        }

        int nCoins = Mathf.Min(12 + stage * 2, 26);
        for (int i = 0; i < nCoins; i++)
        {
            Vector2 p = RandPos(5f, 33f);
            AddCoin(new Vector3(p.x, 1.3f, p.y));
        }

        total = coins.Count;
        collected = 0;
        won = false;
        if (banner != null) banner.gameObject.SetActive(false);
        if (car != null)
        {
            car.position = new Vector3(0, 1.3f, -3);
            carRb.MoveRotation(Quaternion.identity);
            carRb.linearVelocity = Vector3.zero;
            curSpeed = 0f;
        }
        Refresh();
    }

    // random (x,z) on the field, in an annulus around the car's spawn so nothing spawns on the car
    Vector2 RandPos(float minR, float maxR)
    {
        float a = Random.value * Mathf.PI * 2f;
        float r = Random.Range(minR, maxR);
        return new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r - 3f);
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
        if (hud != null) hud.text = "STAGE " + stage + "     COINS  " + collected + " / " + total;
    }

    public void Collect(Coin c)
    {
        if (c == null || c.taken) return;
        c.taken = true;
        coins.Remove(c);
        collected++;
        Juice.Score();                 // pickup sound
        Juice.Shake(0.12f);            // tiny pop
        Burst(c.transform.position);   // gold particle burst
        Destroy(c.gameObject);
        Refresh();
        if (collected >= total && !won)
        {
            won = true;
            winTimer = 2.2f;
            banner.text = "STAGE " + stage + " CLEAR!\nNEXT: STAGE " + (stage + 1);
            banner.gameObject.SetActive(true);
            Juice.Score(); Juice.Shake(0.45f);
        }
    }

    // gold particle burst on pickup (CreatePrimitive cubes — renders reliably in WebGL)
    void Burst(Vector3 pos)
    {
        for (int i = 0; i < 16; i++)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(p.GetComponent<Collider>());
            p.transform.position = pos + Vector3.up * 0.3f;
            p.transform.localScale = Vector3.one * Random.Range(0.16f, 0.34f);
            p.transform.rotation = Random.rotation;
            p.GetComponent<Renderer>().sharedMaterial = goldMat;
            Vector3 dir = (Random.onUnitSphere + Vector3.up * 1.6f).normalized;
            p.AddComponent<Spark>().Init(dir * Random.Range(4.5f, 9f));
        }
    }

    // ---------- loop ----------
    void Update()
    {
        if (won)
        {
            steer = 0f; throttle = 0f;
            winTimer -= Time.deltaTime;
            if (winTimer <= 0f) { stage++; BuildStage(); }
            return;
        }

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

                // veer a consistent way around solid stuff ahead (avoids oscillating in place)
                if (Physics.Raycast(car.position + car.forward * 2.5f + Vector3.up * 0.3f, car.forward, 4f, ~0, QueryTriggerInteraction.Ignore))
                    steer = 1f;

                // unstuck: jammed -> back up and swing the nose
                if (carRb.linearVelocity.magnitude < 1.0f) stuckT += Time.deltaTime; else stuckT = 0f;
                if (stuckT > 0.7f) { throttle = -1f; steer = 1f; if (stuckT > 1.6f) stuckT = 0f; }
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

// A gold shard flung out when a coin is collected: arcs under gravity, spins, shrinks, fades out.
public class Spark : MonoBehaviour
{
    Vector3 vel, spin;
    float age, life = 0.6f;
    public void Init(Vector3 v) { vel = v; spin = Random.insideUnitSphere * 720f; }
    void Update()
    {
        float dt = Time.deltaTime;
        age += dt;
        vel += Vector3.down * 16f * dt;                 // gravity
        transform.position += vel * dt;
        transform.Rotate(spin * dt, Space.World);
        transform.localScale *= Mathf.Max(0f, 1f - dt * 1.6f);
        if (age >= life) Destroy(gameObject);
    }
}
