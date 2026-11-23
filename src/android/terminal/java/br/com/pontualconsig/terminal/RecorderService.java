package br.com.pontualconsig.terminal;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.MediaRecorder;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.WindowManager;

import org.json.JSONObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.Proxy;
import java.net.URL;
import java.net.URLEncoder;
import java.util.Arrays;
import java.util.Calendar;

/**
 * Gravação da tela do celular, como a do PC (MainWindow.Recording): só quando a Central liga, só no horário que
 * ela manda (7h às 18h), em trechos de 1 minuto enviados para POST /device/recording, onde são cifrados e
 * apagados depois de 3 meses. O Android exige que a atendente autorize a gravação ("Começar agora") a cada vez
 * que ela começa; a notificação fixa e a faixa vermelha no Terminal avisam enquanto grava.
 */
public class RecorderService extends Service {
    static final String EXTRA_CODE = "code";
    static final String EXTRA_DATA = "data";
    private static final String CHANNEL = "gravacao";
    private static final long SEGMENT_MS = 60_000;
    private static final int MAX_WIDTH = 720;
    private static final int BITRATE = 150_000;
    private static final long MAX_QUEUE_BYTES = 1024L * 1024 * 1024;

    /** A gravação está autorizada e o serviço está de pé (mesmo fora do horário, parado). */
    static volatile boolean running;
    /** Gravando agora (dentro do horário). */
    static volatile boolean recording;
    // Regra da Central (GET /device/recording), atualizada pelo Core.
    static volatile boolean policyRecord;
    static volatile int startMinute = 7 * 60;
    static volatile int endMinute = 18 * 60;

    private MediaProjection projection;
    private VirtualDisplay display;
    private MediaRecorder recorder;
    private File current;
    private long currentStart;
    private int width;
    private int height;
    private int dpi;
    private HandlerThread thread;
    private Handler worker;
    private boolean uploading;

    static boolean withinHours() {
        Calendar now = Calendar.getInstance();
        int minute = now.get(Calendar.HOUR_OF_DAY) * 60 + now.get(Calendar.MINUTE);
        return minute >= startMinute && minute < endMinute;
    }

    static File queueDir(Context context) {
        File dir = new File(context.getFilesDir(), "gravacoes");
        dir.mkdirs();
        return dir;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (projection != null) {
            return START_NOT_STICKY;
        }

        Intent data = intent == null ? null : (Intent) intent.getParcelableExtra(EXTRA_DATA);
        if (data == null) {
            stopSelf();
            return START_NOT_STICKY;
        }

        try {
            if (Build.VERSION.SDK_INT >= 29) {
                startForeground(2, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
            } else {
                startForeground(2, notification());
            }

            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
            projection = mpm.getMediaProjection(intent.getIntExtra(EXTRA_CODE, 0), data);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "gravação", e);
            projection = null;
        }

        if (projection == null) {
            stopSelf();
            return START_NOT_STICKY;
        }

        thread = new HandlerThread("gravacao");
        thread.start();
        worker = new Handler(thread.getLooper());
        projection.registerCallback(new MediaProjection.Callback() {
            @Override
            public void onStop() {
                // A atendente parou pela notificação do Android, ou o sistema encerrou: o Terminal pede de novo depois.
                worker.post(new Runnable() {
                    @Override
                    public void run() {
                        shutdown();
                    }
                });
            }
        }, worker);

        measure();
        try {
            display = projection.createVirtualDisplay("Pontual", width, height, dpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, null, null, worker);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "tela virtual da gravação", e);
            stopSelf();
            return START_NOT_STICKY;
        }

        running = true;
        worker.post(segment);
        return START_NOT_STICKY;
    }

    private void measure() {
        DisplayMetrics m = new DisplayMetrics();
        ((WindowManager) getSystemService(WINDOW_SERVICE)).getDefaultDisplay().getRealMetrics(m);
        float scale = Math.min(1f, MAX_WIDTH / (float) Math.min(m.widthPixels, m.heightPixels));
        width = Math.max(2, Math.round(m.widthPixels * scale) & ~1);
        height = Math.max(2, Math.round(m.heightPixels * scale) & ~1);
        dpi = Math.max(80, Math.round(m.densityDpi * scale));
    }

    /** A cada minuto fecha o trecho e abre o próximo (ou para, fora do horário). */
    private final Runnable segment = new Runnable() {
        @Override
        public void run() {
            if (display == null) {
                return;
            }

            if (!policyRecord) {
                // A Central desligou a gravação: encerra a autorização.
                stopSelf();
                return;
            }

            MediaRecorder old = recorder;
            File oldFile = current;
            long oldStart = currentStart;
            recorder = null;
            current = null;
            if (withinHours()) {
                try {
                    File file = new File(queueDir(RecorderService.this), "rec.tmp" + System.nanoTime());
                    MediaRecorder r = Build.VERSION.SDK_INT >= 31 ? new MediaRecorder(RecorderService.this) : new MediaRecorder();
                    r.setVideoSource(MediaRecorder.VideoSource.SURFACE);
                    r.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4);
                    r.setVideoEncoder(MediaRecorder.VideoEncoder.H264);
                    r.setVideoSize(width, height);
                    r.setVideoFrameRate(5);
                    r.setVideoEncodingBitRate(BITRATE);
                    r.setOutputFile(file.getAbsolutePath());
                    r.prepare();
                    display.setSurface(r.getSurface());
                    r.start();
                    recorder = r;
                    current = file;
                    currentStart = System.currentTimeMillis();
                } catch (Exception e) {
                    Log.w(Core.TAG, "trecho da gravação", e);
                    display.setSurface(null);
                }
            } else {
                display.setSurface(null);
            }

            recording = recorder != null;
            finish(old, oldFile, oldStart);
            upload();
            worker.postDelayed(this, SEGMENT_MS);
        }
    };

    /** Fecha um trecho e o põe na fila de envio com o horário e a duração no nome. */
    private void finish(MediaRecorder r, File file, long start) {
        if (r == null) {
            return;
        }

        boolean ok;
        try {
            r.stop();
            ok = true;
        } catch (RuntimeException e) {
            // Nenhum quadro chegou (tela apagada o minuto todo): o trecho é descartado.
            ok = false;
        }

        r.release();
        long millis = System.currentTimeMillis() - start;
        if (ok && file != null && file.length() > 0 && millis > 1000) {
            file.renameTo(new File(file.getParentFile(), start + "_" + millis + ".mp4"));
        } else if (file != null) {
            file.delete();
        }
    }

    /** Manda a fila, do mais antigo para o mais novo. Sem rede, tenta de novo no próximo minuto. */
    private void upload() {
        if (uploading) {
            return;
        }

        uploading = true;
        new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    send(RecorderService.this);
                } finally {
                    uploading = false;
                }
            }
        }, "envio-gravacao").start();
    }

    static void send(Context context) {
        Core core = Core.get(context);
        File[] files = queueDir(context).listFiles();
        if (files == null) {
            return;
        }

        Arrays.sort(files);
        long total = 0;
        for (int i = files.length - 1; i >= 0; i--) {
            // Fila cheia (muito tempo sem rede): os trechos mais antigos são descartados.
            total += files[i].length();
            if (total > MAX_QUEUE_BYTES) {
                files[i].delete();
            }
        }

        for (File f : files) {
            String name = f.getName();
            if (!f.exists() || !name.endsWith(".mp4") || core.serverUrl == null || core.token == null) {
                continue;
            }

            String[] parts = name.substring(0, name.length() - 4).split("_");
            try {
                long start = Long.parseLong(parts[0]);
                double seconds = Long.parseLong(parts[1]) / 1000.0;
                if (post(core, f, start, seconds)) {
                    f.delete();
                } else {
                    return;
                }
            } catch (NumberFormatException e) {
                f.delete();
            }
        }
    }

    private static boolean post(Core core, File file, long start, double seconds) {
        try {
            String query = "?startedAt=" + URLEncoder.encode(Core.iso(start), "UTF-8")
                    + "&seconds=" + String.format(java.util.Locale.ROOT, "%.1f", seconds);
            HttpURLConnection c = (HttpURLConnection) new URL(core.serverUrl + "/api/v1/device/recording" + query).openConnection(Proxy.NO_PROXY);
            try {
                c.setRequestMethod("POST");
                c.setConnectTimeout(5000);
                c.setReadTimeout(60000);
                c.setDoOutput(true);
                c.setRequestProperty("Authorization", "Bearer " + core.token);
                c.setRequestProperty("Content-Type", "video/mp4");
                c.setFixedLengthStreamingMode(file.length());
                OutputStream out = c.getOutputStream();
                InputStream in = new FileInputStream(file);
                try {
                    byte[] buffer = new byte[65536];
                    int n;
                    while ((n = in.read(buffer)) > 0) {
                        out.write(buffer, 0, n);
                    }
                } finally {
                    in.close();
                    out.close();
                }

                int status = c.getResponseCode();
                if (status >= 200 && status < 300) {
                    try {
                        JSONObject o = new JSONObject(new String(readSmall(c.getInputStream()), "UTF-8"));
                        policyRecord = o.optBoolean("record", policyRecord);
                    } catch (Exception ignored) {
                        // Resposta sem corpo: o trecho foi aceito do mesmo jeito.
                    }

                    return true;
                }

                // 400: trecho inválido (velho demais, por exemplo). Não adianta reenviar.
                return status == 400;
            } finally {
                c.disconnect();
            }
        } catch (IOException e) {
            return false;
        }
    }

    private static byte[] readSmall(InputStream in) throws IOException {
        try {
            java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
            byte[] b = new byte[4096];
            int n;
            while ((n = in.read(b)) > 0 && out.size() < 65536) {
                out.write(b, 0, n);
            }

            return out.toByteArray();
        } finally {
            in.close();
        }
    }

    private void shutdown() {
        if (worker != null) {
            worker.removeCallbacks(segment);
        }

        MediaRecorder r = recorder;
        File f = current;
        long s = currentStart;
        recorder = null;
        current = null;
        if (display != null) {
            display.setSurface(null);
            display.release();
            display = null;
        }

        finish(r, f, s);
        if (projection != null) {
            MediaProjection p = projection;
            projection = null;
            try {
                p.stop();
            } catch (RuntimeException ignored) {
                // Já parada.
            }
        }

        running = false;
        recording = false;
        upload();
        stopSelf();
    }

    @Override
    public void onDestroy() {
        if (worker != null) {
            worker.post(new Runnable() {
                @Override
                public void run() {
                    if (display != null || projection != null) {
                        shutdown();
                    }

                    thread.quitSafely();
                }
            });
        }

        running = false;
        recording = false;
        super.onDestroy();
    }

    private Notification notification() {
        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            NotificationChannel channel = new NotificationChannel(CHANNEL, "Gravação da tela", NotificationManager.IMPORTANCE_LOW);
            channel.setShowBadge(false);
            manager.createNotificationChannel(channel);
            builder = new Notification.Builder(this, CHANNEL);
        } else {
            builder = new Notification.Builder(this);
        }

        Intent open = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        PendingIntent tap = PendingIntent.getActivity(this, 1, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return builder
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle("A tela deste celular é gravada")
                .setContentText("Das " + (startMinute / 60) + "h às " + (endMinute / 60) + "h, enviada à Central do TI da Pontual.")
                .setContentIntent(tap)
                .setOngoing(true)
                .build();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
