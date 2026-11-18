package br.com.pontualconsig.terminal;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

/**
 * Mantém a ligação com a Central do TI enquanto a atendente está em outro app (no WhatsApp, por exemplo).
 * Sem ele, o Android pode congelar o Terminal em segundo plano e a Central mostraria o aparelho como desligado.
 * A notificação fixa também deixa visível, o tempo todo, que o uso do aparelho é registrado.
 */
public class KeepAliveService extends Service {
    private static final String CHANNEL = "terminal";

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        Core core = Core.get(this);
        if (!core.isEnrolled()) {
            stopSelf();
            return START_NOT_STICKY;
        }

        try {
            Notification notification = buildNotification();
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
            } else {
                startForeground(1, notification);
            }
        } catch (RuntimeException e) {
            // O Android recusou o serviço em primeiro plano: o Terminal segue funcionando enquanto estiver na tela.
            Log.w(Core.TAG, "serviço", e);
            stopSelf();
            return START_NOT_STICKY;
        }

        core.start();
        if (guard == null) {
            guard = new AppGuard(this);
        }

        guard.start();
        return START_STICKY;
    }

    private AppGuard guard;

    @Override
    public void onDestroy() {
        if (guard != null) {
            guard.stop();
        }

        super.onDestroy();
    }

    private Notification buildNotification() {
        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            NotificationChannel channel = new NotificationChannel(CHANNEL, "Terminal Pontual", NotificationManager.IMPORTANCE_LOW);
            channel.setShowBadge(false);
            manager.createNotificationChannel(channel);
            builder = new Notification.Builder(this, CHANNEL);
        } else {
            builder = new Notification.Builder(this);
        }

        Intent open = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        PendingIntent tap = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return builder
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle("Terminal Pontual ativo")
                .setContentText("O uso deste aparelho é registrado pela Pontual.")
                .setContentIntent(tap)
                .setOngoing(true)
                .build();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
