package br.com.pontualconsig.terminal;

import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import org.json.JSONObject;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.Proxy;
import java.net.URL;
import java.security.MessageDigest;
import java.util.Locale;

/**
 * Botão Atualizar do celular, como o do PC: só roda quando a atendente toca no botão (nunca sozinho).
 *   1. lê na Central /baixar/atualizacao/android.json ({"versao":"1.1.0","codigo":2,"sha256":"..."});
 *   2. se o código for maior que o instalado, baixa /baixar/Terminal-Pontual-Android.apk e confere o SHA-256;
 *   3. entrega ao instalador do Android. Ele só aceita se o .apk tiver a mesma assinatura do instalado,
 *      e pergunta "Atualizar este app?" (no bloqueio total, como dono do aparelho, instala direto).
 */
final class Updater {
    interface Done {
        /** Mensagem para a atendente; chamado na linha principal. */
        void message(String text);
    }

    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static volatile boolean busy;
    /** Até quando o vigia de aplicativos deixa o instalador do Android (e a tela de permissão) na frente. */
    static volatile long installerAllowedUntil;

    private Updater() {
    }

    static long installedCode(Context context) {
        try {
            PackageInfo info = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
            return Build.VERSION.SDK_INT >= 28 ? info.getLongVersionCode() : info.versionCode;
        } catch (Exception e) {
            return 0;
        }
    }

    static String installedName(Context context) {
        try {
            return context.getPackageManager().getPackageInfo(context.getPackageName(), 0).versionName;
        } catch (Exception e) {
            return "?";
        }
    }

    /** Consulta e, havendo versão nova, baixa e instala. */
    static void run(final Context context, final String serverUrl, final Done done) {
        if (busy) {
            done.message("A atualização já está em andamento.");
            return;
        }

        busy = true;
        new Thread(new Runnable() {
            @Override
            public void run() {
                String result;
                try {
                    result = work(context.getApplicationContext(), serverUrl);
                } catch (Exception e) {
                    Log.w(Core.TAG, "atualização", e);
                    result = "Não foi possível atualizar agora. Confira o Wi-Fi e tente de novo; se continuar, chame o TI.";
                } finally {
                    busy = false;
                }

                final String text = result;
                MAIN.post(new Runnable() {
                    @Override
                    public void run() {
                        done.message(text);
                    }
                });
            }
        }, "atualizacao").start();
    }

    private static String work(Context context, String serverUrl) throws Exception {
        JSONObject info = new JSONObject(new String(Api.fetch(serverUrl + "/baixar/atualizacao/android.json"), "UTF-8"));
        long code = info.optLong("codigo", 0);
        String name = info.optString("versao", "");
        String sha = info.optString("sha256", "").trim().toLowerCase(Locale.ROOT);
        if (code <= installedCode(context)) {
            return "Este celular já está na versão mais nova (" + installedName(context) + ").";
        }

        if (sha.length() != 64) {
            return "A atualização publicada na Central está incompleta. Chame o TI.";
        }

        // Baixa direto para a sessão do instalador, conferindo o SHA-256 no caminho.
        PackageInstaller installer = context.getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        params.setAppPackageName(context.getPackageName());
        if (Build.VERSION.SDK_INT >= 31) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        }

        int id = installer.createSession(params);
        PackageInstaller.Session session = installer.openSession(id);
        boolean committed = false;
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            HttpURLConnection c = (HttpURLConnection) new URL(serverUrl + "/baixar/Terminal-Pontual-Android.apk").openConnection(Proxy.NO_PROXY);
            c.setConnectTimeout(5000);
            c.setReadTimeout(30000);
            c.setUseCaches(false);
            try {
                if (c.getResponseCode() != 200) {
                    return "A Central não entregou o arquivo da atualização (erro " + c.getResponseCode() + "). Chame o TI.";
                }

                InputStream in = c.getInputStream();
                OutputStream out = session.openWrite("terminal.apk", 0, -1);
                try {
                    byte[] buffer = new byte[65536];
                    long total = 0;
                    int n;
                    while ((n = in.read(buffer)) > 0) {
                        total += n;
                        if (total > 200L * 1024 * 1024) {
                            throw new java.io.IOException("Arquivo grande demais.");
                        }

                        digest.update(buffer, 0, n);
                        out.write(buffer, 0, n);
                    }

                    session.fsync(out);
                } finally {
                    out.close();
                    in.close();
                }
            } finally {
                c.disconnect();
            }

            StringBuilder hex = new StringBuilder();
            for (byte b : digest.digest()) {
                hex.append(String.format(Locale.ROOT, "%02x", b));
            }

            if (!hex.toString().equals(sha)) {
                return "O arquivo da atualização chegou diferente do publicado. Nada foi instalado. Chame o TI.";
            }

            installerAllowedUntil = System.currentTimeMillis() + 5 * 60 * 1000;
            Intent status = new Intent(context, Receiver.class).setPackage(context.getPackageName());
            int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
            session.commit(PendingIntent.getBroadcast(context, id, status, flags).getIntentSender());
            committed = true;
            return "Instalando a versão " + name + ". Se o Android perguntar, toque em Atualizar (ou Instalar).";
        } finally {
            if (!committed) {
                session.abandon();
            }

            session.close();
        }
    }

    /** Resposta do instalador: pede a confirmação da atendente ou avisa o resultado. */
    public static final class Receiver extends BroadcastReceiver {
        @Override
        public void onReceive(Context context, Intent intent) {
            int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
            if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                Intent confirm = intent.getParcelableExtra(Intent.EXTRA_INTENT);
                if (confirm != null) {
                    installerAllowedUntil = System.currentTimeMillis() + 5 * 60 * 1000;
                    try {
                        context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                    } catch (RuntimeException e) {
                        Log.w(Core.TAG, "confirmação da atualização", e);
                    }
                }
            } else if (status != PackageInstaller.STATUS_SUCCESS) {
                installerAllowedUntil = 0;
                String why = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
                Log.w(Core.TAG, "atualização recusada: " + status + " " + why);
                android.widget.Toast.makeText(context, status == PackageInstaller.STATUS_FAILURE_ABORTED
                        ? "Atualização cancelada." : "A atualização não foi instalada. Chame o TI.", android.widget.Toast.LENGTH_LONG).show();
            }
            // Sucesso: o Android fecha o app antigo e abre o novo (o Terminal é a tela de início).
        }
    }
}
