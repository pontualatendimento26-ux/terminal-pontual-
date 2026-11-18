package br.com.pontualconsig.terminal;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Estado do Terminal no aparelho e conversa com a Central do TI: cadastro, heartbeat a cada 15 segundos,
 * configuração e fila de eventos. Existe uma só instância por processo; a tela (MainActivity) e o serviço
 * (KeepAliveService) usam a mesma. Os campos de estado só mudam na linha de execução principal.
 */
final class Core {
    static final String TAG = "Pontual";
    static final String STATE_PENDING = "pending";
    static final String STATE_ACTIVE = "active";
    static final String STATE_MAINTENANCE = "maintenance";

    private static final long HEARTBEAT_MS = 15000;
    private static final int MAX_QUEUE = 500;

    interface Listener {
        void onStatus();

        void onConfig();

        void onUnenrolled();
    }

    static final class Item {
        String id = "";
        String name = "";
        String kind = "site";
        String target = "";
        String group = "";
        List<String> hosts = new ArrayList<>();

        boolean isProgram() {
            return "program".equals(kind);
        }
    }

    private static Core instance;

    static synchronized Core get(Context context) {
        if (instance == null) {
            instance = new Core(context.getApplicationContext());
        }

        return instance;
    }

    private final Context app;
    private final SharedPreferences prefs;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService net = Executors.newSingleThreadExecutor();
    private final ArrayDeque<JSONObject> queue = new ArrayDeque<>();
    private final String sessionStartedAt = iso(System.currentTimeMillis());

    // Lidos também pela linha de rede e pela do WebView.
    volatile String serverUrl;
    volatile String token;

    List<Item> items = Collections.emptyList();
    boolean hasConfig;
    int version;
    HostPolicy policy = HostPolicy.EMPTY;
    boolean observationMode;
    String state = STATE_ACTIVE;
    String maintenanceReason = "";
    String maintenanceUntil = "";
    String attendantName = "";
    Boolean online;

    /** Nomes dos sites abertos na tela, mantidos pela MainActivity. */
    final List<String> tabsInUse = new ArrayList<>();
    /** App de fora aberto por último (o WhatsApp), enquanto a atendente não volta ao Terminal. */
    Item external;
    Listener listener;

    private boolean running;
    private boolean ticking;
    private boolean sessionRecorded;

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            tick();
        }
    };

    private Core(Context app) {
        this.app = app;
        prefs = app.getSharedPreferences("terminal", Context.MODE_PRIVATE);
        serverUrl = prefs.getString("serverUrl", "");
        token = prefs.getString("token", "");
    }

    boolean isEnrolled() {
        return !serverUrl.isEmpty() && !token.isEmpty();
    }

    void saveEnrollment(String url, String newToken) {
        serverUrl = url;
        token = newToken;
        prefs.edit().putString("serverUrl", url).putString("token", newToken).apply();
    }

    /** Esquece o cadastro e volta ao estado de aparelho novo. */
    void clearEnrollment() {
        stop();
        serverUrl = "";
        token = "";
        prefs.edit().clear().apply();
        items = Collections.emptyList();
        hasConfig = false;
        policy = HostPolicy.EMPTY;
        state = STATE_ACTIVE;
        attendantName = "";
        online = null;
        external = null;
        tabsInUse.clear();
        sessionRecorded = false;
        synchronized (queue) {
            queue.clear();
        }
    }

    void start() {
        if (running || !isEnrolled()) {
            return;
        }

        running = true;
        if (!sessionRecorded) {
            sessionRecorded = true;
            record("session_started", null, null);
        }

        main.removeCallbacks(tick);
        main.post(tick);
    }

    void stop() {
        running = false;
        ticking = false;
        main.removeCallbacks(tick);
    }

    Item find(String id) {
        for (Item item : items) {
            if (item.id.equals(id)) {
                return item;
            }
        }

        return null;
    }

    /** Nome do aparelho no formato que o servidor aceita: letras, números e hífen. */
    String hostname() {
        String model = Build.MODEL == null ? "" : Build.MODEL;
        String suffix = "";
        try {
            String androidId = Settings.Secure.getString(app.getContentResolver(), Settings.Secure.ANDROID_ID);
            if (androidId != null && androidId.length() >= 4) {
                suffix = "-" + androidId.substring(androidId.length() - 4);
            }
        } catch (RuntimeException ignored) {
            // Sem o identificador, o nome fica só com o modelo.
        }

        String name = ("CEL-" + model).replaceAll("[^A-Za-z0-9]+", "-");
        if (name.length() > 50) {
            name = name.substring(0, 50);
        }

        name = (name + suffix).replaceAll("-{2,}", "-").replaceAll("-+$", "");
        return name.toUpperCase(Locale.ROOT);
    }

    /** Vai no campo "windows" do cadastro: é o que a Central mostra como sistema da máquina. */
    static String systemLabel() {
        String label = "Android " + Build.VERSION.RELEASE + " - " + Build.MANUFACTURER + " " + Build.MODEL;
        return label.length() > 100 ? label.substring(0, 100) : label;
    }

    // ------------------------------------------------------------------ eventos

    void record(String type, String item, String detail) {
        if (!isEnrolled()) {
            return;
        }

        try {
            JSONObject e = new JSONObject();
            e.put("type", type);
            if (item != null && !item.isEmpty()) {
                e.put("item", item);
            }

            if (detail != null && !detail.isEmpty()) {
                e.put("detail", detail);
            }

            e.put("at", iso(System.currentTimeMillis()));
            synchronized (queue) {
                queue.addLast(e);
                while (queue.size() > MAX_QUEUE) {
                    queue.removeFirst();
                }
            }
        } catch (JSONException ex) {
            Log.w(TAG, "evento", ex);
            return;
        }

        final String url = serverUrl;
        final String tk = token;
        net.execute(new Runnable() {
            @Override
            public void run() {
                flush(url, tk);
            }
        });
    }

    /** Entrega a fila ao servidor. Sem resposta, os eventos ficam para o próximo ciclo. Roda na linha de rede. */
    private void flush(String url, String tk) {
        while (true) {
            JSONArray batch = new JSONArray();
            synchronized (queue) {
                for (JSONObject e : queue) {
                    batch.put(e);
                    if (batch.length() >= 50) {
                        break;
                    }
                }
            }

            if (batch.length() == 0 || !tk.equals(token)) {
                return;
            }

            boolean delivered;
            try {
                Api.call("POST", url, tk, "/device/events", new JSONObject().put("events", batch));
                delivered = true;
            } catch (Api.HttpError e) {
                if (e.status == 401) {
                    return;
                }

                // O servidor recusou o lote de vez (400, por exemplo): repetir não adianta.
                Log.w(TAG, "eventos recusados: " + e.getMessage());
                delivered = true;
            } catch (IOException | JSONException e) {
                return;
            }

            if (delivered) {
                synchronized (queue) {
                    for (int i = 0; i < batch.length() && !queue.isEmpty(); i++) {
                        if (queue.peekFirst() == batch.opt(i)) {
                            queue.removeFirst();
                        }
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ ciclo com o servidor

    private static final class TickResult {
        boolean reached;
        boolean unauthorized;
        JSONObject heartbeat;
        JSONObject config;
    }

    private void tick() {
        if (!running || ticking) {
            return;
        }

        ticking = true;
        final String url = serverUrl;
        final String tk = token;
        final boolean had = hasConfig;
        final int known = version;
        final JSONObject body = new JSONObject();
        try {
            JSONArray apps = new JSONArray();
            for (String name : tabsInUse) {
                apps.put(name);
            }

            if (external != null) {
                apps.put(external.name);
            }

            body.put("appsInUse", apps);
            body.put("sessionStartedAt", sessionStartedAt);
        } catch (JSONException e) {
            Log.w(TAG, "heartbeat", e);
        }

        net.execute(new Runnable() {
            @Override
            public void run() {
                final TickResult r = new TickResult();
                try {
                    JSONObject hb = Api.call("POST", url, tk, "/device/heartbeat", body);
                    r.reached = true;
                    r.heartbeat = hb == null ? new JSONObject() : hb;
                    boolean pending = STATE_PENDING.equals(str(r.heartbeat, "state"));
                    if (!pending && (!had || r.heartbeat.optInt("configVersion", known) != known)) {
                        r.config = Api.call("GET", url, tk, "/device/config", null);
                    }
                } catch (Api.HttpError e) {
                    // O servidor respondeu, mas com erro: há conexão.
                    r.reached = true;
                    r.unauthorized = e.status == 401;
                    if (!r.unauthorized) {
                        Log.w(TAG, "servidor: " + e.getMessage());
                    }
                } catch (JSONException e) {
                    r.reached = true;
                    Log.w(TAG, "resposta ilegível", e);
                } catch (IOException e) {
                    r.reached = false;
                }

                if (r.reached && !r.unauthorized) {
                    flush(url, tk);
                }

                main.post(new Runnable() {
                    @Override
                    public void run() {
                        apply(r, tk);
                    }
                });
            }
        });
    }

    private void apply(TickResult r, String tk) {
        // Resultado de um cadastro que já não vale (refeito ou removido enquanto a rede respondia).
        if (!running || !tk.equals(token)) {
            return;
        }

        ticking = false;
        if (r.unauthorized) {
            // 401: o aparelho foi removido na Central e precisa de novo cadastro.
            clearEnrollment();
            if (listener != null) {
                listener.onUnenrolled();
            }

            return;
        }

        online = r.reached;
        boolean configChanged = false;
        if (r.heartbeat != null) {
            String st = str(r.heartbeat, "state");
            if (STATE_PENDING.equals(st) || STATE_ACTIVE.equals(st) || STATE_MAINTENANCE.equals(st)) {
                state = st;
            }

            observationMode = r.heartbeat.optBoolean("observationMode", false);
            String name = str(r.heartbeat, "attendantName");
            if (!name.trim().isEmpty()) {
                attendantName = name;
            }

            JSONObject m = r.heartbeat.optJSONObject("maintenance");
            maintenanceReason = m == null ? "" : str(m, "reason");
            maintenanceUntil = m == null ? "" : str(m, "until");

            if (STATE_PENDING.equals(state)) {
                // Aparelho ainda não aprovado (ou que voltou a ficar pendente): nada fica aberto.
                if (hasConfig) {
                    items = Collections.emptyList();
                    hasConfig = false;
                    policy = HostPolicy.EMPTY;
                    configChanged = true;
                }
            } else if (r.config != null) {
                useConfig(r.config);
                configChanged = true;
            }
        }

        if (listener != null) {
            listener.onStatus();
            if (configChanged) {
                listener.onConfig();
            }
        }

        main.removeCallbacks(tick);
        main.postDelayed(tick, HEARTBEAT_MS);
    }

    private void useConfig(JSONObject c) {
        List<Item> list = new ArrayList<>();
        List<String> hosts = new ArrayList<>();
        JSONArray array = c.optJSONArray("items");
        for (int i = 0; array != null && i < array.length(); i++) {
            JSONObject o = array.optJSONObject(i);
            if (o == null) {
                continue;
            }

            Item item = new Item();
            item.id = str(o, "id");
            item.name = str(o, "name");
            item.target = str(o, "target");
            if (item.id.trim().isEmpty() || item.name.trim().isEmpty() || item.target.trim().isEmpty()) {
                continue;
            }

            item.kind = "program".equals(str(o, "kind")) ? "program" : "site";
            item.group = str(o, "group");
            JSONArray hs = o.optJSONArray("hosts");
            for (int j = 0; hs != null && j < hs.length(); j++) {
                String h = hs.isNull(j) ? "" : hs.optString(j, "");
                if (!h.trim().isEmpty()) {
                    item.hosts.add(h);
                }
            }

            hosts.addAll(item.hosts);
            list.add(item);
        }

        items = list;
        version = c.optInt("version", 0);
        hasConfig = true;
        policy = new HostPolicy(hosts);
        String name = str(c, "attendantName");
        if (!name.trim().isEmpty()) {
            attendantName = name;
        }
    }

    /** O org.json devolve o texto "null" para um campo nulo; aqui campo nulo ou ausente vira texto vazio. */
    static String str(JSONObject o, String key) {
        return o == null || o.isNull(key) ? "" : o.optString(key, "");
    }

    static String iso(long millis) {
        SimpleDateFormat f = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        f.setTimeZone(TimeZone.getTimeZone("UTC"));
        return f.format(new Date(millis));
    }
}
