package br.com.pontualconsig.terminal;

import android.Manifest;
import android.app.Activity;
import android.app.ActivityManager;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.LauncherActivityInfo;
import android.content.pm.LauncherApps;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Process;
import android.os.UserHandle;
import android.os.UserManager;
import android.util.Log;

import java.util.ArrayList;
import java.util.List;

/**
 * Bloqueio do aparelho (modo "lock task" do Android). Só tem efeito quando o app é o dono do aparelho
 * (Device Owner), o que nesta versão de teste é feito pelo cabo USB (ver bloquear.ps1). Sem isso, o app
 * funciona como um aplicativo comum e nada aqui é aplicado.
 *
 * De propósito, esta versão NÃO bloqueia a restauração de fábrica nem desliga a depuração USB: são as duas
 * portas de saída do teste e só podem ser fechadas depois que o desfazer tiver funcionado na prática.
 */
final class Lock {
    /** WhatsApp Business e WhatsApp comum, nesta ordem de preferência. */
    static final String[] WHATSAPP = {"com.whatsapp.w4b", "com.whatsapp"};

    private Lock() {
    }

    static boolean isOwner(Context context) {
        DevicePolicyManager dpm = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
        return dpm != null && dpm.isDeviceOwnerApp(context.getPackageName());
    }

    /** Um WhatsApp que dá para abrir: do aparelho principal ou de outro perfil (o duplicado da Samsung). */
    static final class App {
        final String pkg;
        final UserHandle user;
        final ComponentName component;
        final boolean duplicate;
        final String label;

        App(String pkg, UserHandle user, ComponentName component, boolean duplicate) {
            this.pkg = pkg;
            this.user = user;
            this.component = component;
            this.duplicate = duplicate;
            this.label = (pkg.endsWith(".w4b") ? "WhatsApp Business" : "WhatsApp") + (duplicate ? " (duplicado)" : "");
        }
    }

    /**
     * Todos os WhatsApp abríveis, inclusive os de outros perfis (Dual Messenger da Samsung cria um perfil
     * separado). Usa LauncherApps, a mesma API das telas de início para mostrar apps de outros perfis.
     */
    static List<App> whatsAppTargets(Context context) {
        List<App> found = new ArrayList<>();
        UserHandle me = Process.myUserHandle();
        LauncherApps launcher = context.getSystemService(LauncherApps.class);
        List<UserHandle> users = new ArrayList<>();
        users.add(me);
        try {
            UserManager um = context.getSystemService(UserManager.class);
            if (um != null) {
                for (UserHandle u : um.getUserProfiles()) {
                    if (!u.equals(me)) {
                        users.add(u);
                    }
                }
            }
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "perfis", e);
        }

        for (UserHandle user : users) {
            for (String pkg : WHATSAPP) {
                try {
                    List<LauncherActivityInfo> list = launcher == null ? null : launcher.getActivityList(pkg, user);
                    if (list != null && !list.isEmpty()) {
                        found.add(new App(pkg, user, list.get(0).getComponentName(), !user.equals(me)));
                    }
                } catch (RuntimeException e) {
                    Log.w(Core.TAG, "procurar " + pkg + " em " + user, e);
                }
            }
        }

        // Sem LauncherApps (não deveria acontecer): pelo menos os do aparelho principal.
        if (found.isEmpty()) {
            for (String pkg : installedWhatsApp(context)) {
                Intent i = context.getPackageManager().getLaunchIntentForPackage(pkg);
                if (i != null && i.getComponent() != null) {
                    found.add(new App(pkg, me, i.getComponent(), false));
                }
            }
        }

        Log.i(Core.TAG, "WhatsApp encontrados: " + found.size() + " em " + users.size() + " perfil(is)");
        return found;
    }

    /** Abre um WhatsApp. Devolve false se o Android recusou. */
    static boolean launch(Context context, App app) {
        try {
            if (app.user.equals(Process.myUserHandle())) {
                Intent i = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
                        .setComponent(app.component).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                context.startActivity(i);
            } else {
                context.getSystemService(LauncherApps.class).startMainActivity(app.component, app.user, null, null);
            }

            return true;
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "abrir " + app.label, e);
            return false;
        }
    }

    static List<String> installedWhatsApp(Context context) {
        List<String> found = new ArrayList<>();
        PackageManager pm = context.getPackageManager();
        for (String pkg : WHATSAPP) {
            if (pm.getLaunchIntentForPackage(pkg) != null) {
                found.add(pkg);
            }
        }

        return found;
    }

    private static ComponentName admin(Context context) {
        return new ComponentName(context, AdminReceiver.class);
    }

    private static ComponentName homeAlias(Context context) {
        return new ComponentName(context.getPackageName(), context.getPackageName() + ".Inicio");
    }

    /**
     * Prende o aparelho no Terminal. O WhatsApp só entra na lista do que pode abrir se a Central liberou o
     * item dele para este aparelho. O botão de início do Android passa a voltar para o Terminal.
     */
    static void apply(Activity activity, boolean allowWhatsApp) {
        if (!isOwner(activity)) {
            return;
        }

        try {
            DevicePolicyManager dpm = (DevicePolicyManager) activity.getSystemService(Context.DEVICE_POLICY_SERVICE);
            ComponentName admin = admin(activity);
            String pkg = activity.getPackageName();

            List<String> allowed = new ArrayList<>();
            allowed.add(pkg);
            if (allowWhatsApp) {
                allowed.addAll(installedWhatsApp(activity));
            }

            dpm.setLockTaskPackages(admin, allowed.toArray(new String[0]));
            if (Build.VERSION.SDK_INT >= 28) {
                // Botão de início, notificações (para ver mensagem nova do WhatsApp), relógio e bateria,
                // menu de desligar e a tela de bloqueio normal do aparelho.
                dpm.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_HOME
                        | DevicePolicyManager.LOCK_TASK_FEATURE_NOTIFICATIONS
                        | DevicePolicyManager.LOCK_TASK_FEATURE_SYSTEM_INFO
                        | DevicePolicyManager.LOCK_TASK_FEATURE_GLOBAL_ACTIONS
                        | DevicePolicyManager.LOCK_TASK_FEATURE_KEYGUARD);
            }

            activity.getPackageManager().setComponentEnabledSetting(homeAlias(activity),
                    PackageManager.COMPONENT_ENABLED_STATE_ENABLED, PackageManager.DONT_KILL_APP);
            IntentFilter home = new IntentFilter(Intent.ACTION_MAIN);
            home.addCategory(Intent.CATEGORY_HOME);
            home.addCategory(Intent.CATEGORY_DEFAULT);
            dpm.addPersistentPreferredActivity(admin, home, homeAlias(activity));

            if (Build.VERSION.SDK_INT >= 33) {
                // Com o aparelho preso, a pergunta de permissão do Android pode não aparecer.
                dpm.setPermissionGrantState(admin, pkg, Manifest.permission.POST_NOTIFICATIONS,
                        DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED);
            }

            if (!isLocked(activity)) {
                activity.startLockTask();
            }
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "aplicar bloqueio", e);
        }
    }

    static boolean isLocked(Context context) {
        ActivityManager am = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
        return am != null && am.getLockTaskModeState() != ActivityManager.LOCK_TASK_MODE_NONE;
    }

    /** Solta o aparelho agora, sem deixar de ser o dono: o bloqueio volta quando o Terminal for aberto de novo. */
    static void release(Activity activity) {
        try {
            if (isLocked(activity)) {
                activity.stopLockTask();
            }
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "soltar bloqueio", e);
        }
    }

    /**
     * Desfaz tudo: solta o aparelho, devolve o botão de início ao Android e deixa de ser o dono do aparelho.
     * Devolve true se o app deixou mesmo de ser o dono. Se devolver false, o caminho é o comando pelo cabo
     * (desbloquear.ps1).
     */
    static boolean remove(Activity activity) {
        release(activity);
        if (!isOwner(activity)) {
            disableHomeAlias(activity);
            return true;
        }

        DevicePolicyManager dpm = (DevicePolicyManager) activity.getSystemService(Context.DEVICE_POLICY_SERVICE);
        ComponentName admin = admin(activity);
        String pkg = activity.getPackageName();
        try {
            dpm.clearPackagePersistentPreferredActivities(admin, pkg);
            dpm.setLockTaskPackages(admin, new String[0]);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "limpar bloqueio", e);
        }

        disableHomeAlias(activity);
        try {
            // Marcado como obsoleto pelo Android, mas é o único jeito de o próprio app abrir mão do aparelho.
            dpm.clearDeviceOwnerApp(pkg);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "deixar de ser dono", e);
        }

        try {
            dpm.removeActiveAdmin(admin);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "remover administrador", e);
        }

        return !isOwner(activity);
    }

    private static void disableHomeAlias(Context context) {
        try {
            context.getPackageManager().setComponentEnabledSetting(homeAlias(context),
                    PackageManager.COMPONENT_ENABLED_STATE_DEFAULT, PackageManager.DONT_KILL_APP);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "botão de início", e);
        }
    }
}
