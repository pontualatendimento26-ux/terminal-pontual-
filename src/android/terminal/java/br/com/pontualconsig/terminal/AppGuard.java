package br.com.pontualconsig.terminal;

import android.app.AppOpsManager;
import android.app.usage.UsageEvents;
import android.app.usage.UsageStatsManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.Process;
import android.telecom.TelecomManager;
import android.util.Log;

import java.util.Arrays;
import java.util.HashSet;
import java.util.Set;

/**
 * Vigia de aplicativos (celular sem bloqueio total). A cada 1,5 s vê, pelo "acesso aos dados de uso" do
 * Android (UsageStatsManager), qual aplicativo veio para a frente. Se não for autorizado: avisa a Central do
 * TI (evento app_blocked), traz o Terminal de volta e mostra a mensagem à atendente.
 *
 * Só vê o que é aberto no perfil principal do aparelho. O que roda no perfil do WhatsApp duplicado
 * (Dual Messenger) não aparece aqui, e por isso não é barrado.
 *
 * Fica parado quando: o aparelho não está cadastrado, não foi aprovado, está em manutenção (o TI pode mexer
 * à vontade) ou o Android não deu o acesso aos dados de uso.
 */
final class AppGuard {
    private static final long POLL_MS = 1500;
    private static final long QUIET_MS = 3000;
    private static final long SAME_APP_REPORT_MS = 60000;

    /** Partes do próprio Android que aparecem por cima dos apps e não são "sair do Terminal". */
    private static final Set<String> SYSTEM = new HashSet<>(Arrays.asList(
            "android",
            "com.android.systemui",
            "com.android.intentresolver",
            "com.android.permissioncontroller",
            "com.google.android.permissioncontroller",
            "com.google.android.gms",
            "com.android.documentsui",
            "com.google.android.documentsui",
            "com.android.providers.media.module",
            "com.google.android.providers.media.module",
            // Tela de início e apps recentes da Samsung (os recentes são parte dela).
            "com.sec.android.app.launcher",
            // Ligações: telefone e tela de chamada.
            "com.android.server.telecom",
            "com.android.phone",
            "com.samsung.android.incallui",
            "com.samsung.android.dialer",
            "com.google.android.dialer",
            "com.android.dialer"));

    private final Context app;
    private final Handler main = new Handler(Looper.getMainLooper());
    private boolean running;
    private boolean warnedNoAccess;
    private long lastQuery;
    private long quietUntil;
    private String lastReported = "";
    private long lastReportedAt;

    private final Runnable poll = new Runnable() {
        @Override
        public void run() {
            try {
                check();
            } catch (RuntimeException e) {
                Log.w(Core.TAG, "vigia", e);
            }

            if (running) {
                main.postDelayed(this, POLL_MS);
            }
        }
    };

    AppGuard(Context context) {
        app = context.getApplicationContext();
    }

    void start() {
        if (running) {
            return;
        }

        running = true;
        lastQuery = System.currentTimeMillis();
        main.post(poll);
    }

    void stop() {
        running = false;
        main.removeCallbacks(poll);
    }

    static boolean hasUsageAccess(Context context) {
        AppOpsManager ops = (AppOpsManager) context.getSystemService(Context.APP_OPS_SERVICE);
        if (ops == null) {
            return false;
        }

        int mode = Build.VERSION.SDK_INT >= 29
                ? ops.unsafeCheckOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), context.getPackageName())
                : ops.checkOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), context.getPackageName());
        return mode == AppOpsManager.MODE_ALLOWED;
    }

    private void check() {
        Core core = Core.get(app);
        long now = System.currentTimeMillis();
        if (!core.isEnrolled() || !Core.STATE_ACTIVE.equals(core.state) || !core.hasConfig) {
            lastQuery = now;
            return;
        }

        if (!hasUsageAccess(app)) {
            if (!warnedNoAccess) {
                warnedNoAccess = true;
                Log.w(Core.TAG, "vigia parado: falta o acesso aos dados de uso (instalar.ps1 libera)");
            }

            lastQuery = now;
            return;
        }

        UsageStatsManager usm = (UsageStatsManager) app.getSystemService(Context.USAGE_STATS_SERVICE);
        if (usm == null) {
            return;
        }

        // O último aplicativo que veio para a frente desde a consulta anterior.
        UsageEvents events = usm.queryEvents(lastQuery - 500, now);
        lastQuery = now;
        UsageEvents.Event e = new UsageEvents.Event();
        String front = null;
        while (events.hasNextEvent()) {
            events.getNextEvent(e);
            if (e.getEventType() == UsageEvents.Event.MOVE_TO_FOREGROUND) {
                front = e.getPackageName();
            }
        }

        if (front == null || now < quietUntil || isAllowed(core, front)) {
            return;
        }

        String label = labelOf(front);
        Log.i(Core.TAG, "aplicativo não autorizado: " + front);
        if (!front.equals(lastReported) || now - lastReportedAt > SAME_APP_REPORT_MS) {
            core.record("app_blocked", null, label);
            lastReported = front;
            lastReportedAt = now;
        }

        quietUntil = now + QUIET_MS;
        bringBack("Você entrou num aplicativo não autorizado (" + label + "). O TI foi notificado.");
    }

    private boolean isAllowed(Core core, String pkg) {
        if (pkg.equals(app.getPackageName()) || SYSTEM.contains(pkg)) {
            return true;
        }

        try {
            TelecomManager telecom = (TelecomManager) app.getSystemService(Context.TELECOM_SERVICE);
            if (telecom != null && pkg.equals(telecom.getDefaultDialerPackage())) {
                return true;
            }
        } catch (RuntimeException ignored) {
            // Sem o serviço de telefonia: segue a lista fixa.
        }

        // O WhatsApp só é autorizado se a Central liberou o item dele para este aparelho.
        if (Arrays.asList(Lock.WHATSAPP).contains(pkg)) {
            for (Core.Item item : core.items) {
                if (MainActivity.isWhatsApp(item)) {
                    return true;
                }
            }
        }

        return false;
    }

    private String labelOf(String pkg) {
        try {
            PackageManager pm = app.getPackageManager();
            ApplicationInfo info = pm.getApplicationInfo(pkg, 0);
            CharSequence label = pm.getApplicationLabel(info);
            if (label != null && label.toString().trim().length() > 0) {
                return label.toString().trim();
            }
        } catch (PackageManager.NameNotFoundException | RuntimeException ignored) {
            // Sem o nome legível: vai o identificador do pacote.
        }

        return pkg;
    }

    /** Traz o Terminal para a frente, com a mensagem. Abrir tela a partir do serviço exige "sobrepor a outros apps". */
    private void bringBack(String message) {
        Intent intent = MainActivity.frontIntent(app).putExtra(MainActivity.EXTRA_WARNING, message);
        try {
            app.startActivity(intent);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "trazer o Terminal de volta", e);
        }
    }
}
