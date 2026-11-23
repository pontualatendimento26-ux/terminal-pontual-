package br.com.pontualconsig.terminal;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ComponentName;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.SystemClock;
import android.provider.Settings;
import android.text.InputType;
import android.text.TextUtils;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.ConsoleMessage;
import android.webkit.CookieManager;
import android.webkit.DownloadListener;
import android.webkit.GeolocationPermissions;
import android.webkit.JavascriptInterface;
import android.webkit.PermissionRequest;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.webkit.WebStorage;
import android.media.projection.MediaProjectionConfig;
import android.media.projection.MediaProjectionManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.net.MalformedURLException;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Tela do Terminal no celular. Faz o mesmo papel da janela do programa do PC (MainWindow, em
 * Pontual.Terminal.Shell): mostra a tela inicial da atendente, que é a mesma página servida pela Central
 * em /terminal/, e abre cada site liberado em tela cheia, sob uma barra fina. Segue docs/V1-CONTRATO.md.
 *
 * Diferença em relação ao PC: o item do WhatsApp Web abre o aplicativo WhatsApp instalado no aparelho.
 */
public class MainActivity extends Activity implements Core.Listener {
    private static final int BLUE = 0xFF0D47A1;
    private static final int GREEN = 0xFF2E9E5B;
    private static final int INK = 0xFF0F172A;
    private static final int SOFT = 0xFF64748B;
    private static final int LINE = 0xFFE6EBF2;
    private static final int RUBY = 0xFFB91C1C;
    // Só um exemplo de formato: o endereço real da Central muda de rede para rede (e pode mudar
    // quando o roteador redistribui os endereços), então o campo começa vazio.
    private static final String EXAMPLE_SERVER = "http://192.168.0.10:5080";

    /** Ajustes da tela inicial para a largura de um celular. A página original foi desenhada para monitor. */
    private static final String PHONE_CSS =
            "@media (max-width: 700px) {"
            + ".caixa-pagina{width:calc(100% - 32px)}"
            + ".topo>div{min-height:56px;gap:12px;flex-wrap:nowrap;padding:6px 0}"
            + ".logo{width:118px;height:34px}.logo img{width:130px;height:130px}"
            + ".topo .fim{gap:10px}.eu>span:last-child{display:none}"
            + ".heroi{padding:28px 0 16px}.heroi h1{font-size:28px}.heroi>div>p{font-size:15px}"
            + ".procura{margin-top:20px}.procura input{height:52px;font-size:16px;padding:0 20px 0 50px}"
            + ".procura>svg{left:18px;top:15px}.procura kbd{display:none}"
            + ".destaques{grid-template-columns:repeat(2,1fr);gap:10px}.destaque{padding:14px;gap:10px}"
            + ".lista-sites{grid-template-columns:1fr}"
            + ".todos-topo h2{font-size:18px}"
            + ".guias{width:100%;overflow-x:auto}.guias button{flex:none;white-space:nowrap}"
            + ".estado h1,.manut h2{font-size:26px}.estado p,.manut p{font-size:15px}"
            + ".rodape>div{gap:10px}.rodape .fim{margin-left:0}"
            + ".avisos{top:68px;right:16px}.folha{padding:22px}"
            + "}";

    private static final class Tab {
        final Core.Item item;
        final WebView view;
        String blockedHost;
        String lastObservedHost = "";
        long lastObservedAt;
        String lastBlockedHost = "";
        long lastBlockedAt;
        boolean closed;

        Tab(Core.Item item, WebView view) {
            this.item = item;
            this.view = view;
        }
    }

    private Core core;
    private FrameLayout root;
    private FrameLayout content;
    private LinearLayout mainLayout;
    private LinearLayout barBox;
    private LinearLayout blockedPanel;
    private LinearLayout overlay;
    private ScrollView enrollView;
    private TextView barTitle;
    private TextView helpButton;
    private TextView blockedText;
    private TextView overlayText;
    private TextView enrollError;
    private Button blockedBack;
    private Button enrollButton;
    private EditText enrollServer;
    private EditText enrollCode;
    private ProgressBar progress;
    private WebView home;
    private final List<Tab> tabs = new ArrayList<>();
    private Tab active;
    private boolean homeReady;
    private boolean homeFailed;
    private volatile boolean homeLoadFailed;
    private boolean away;
    private boolean resumed;
    private boolean askedNotifications;
    private int taps;
    // Celular compartilhado, IA, gravação e atualização (2026-11-23).
    private Assistant assistant;
    private TextView bubble;
    private LinearLayout footer;
    private TextView footerWho;
    private TextView footerRec;
    private TextView footerUpdate;
    private boolean picking;
    private boolean askingRecording;
    private long askedRecordingAt;
    private static final int REQUEST_RECORDING = 41;
    private long firstTapAt;

    // ------------------------------------------------------------------ ciclo de vida

    /** A cópia do Terminal que está valendo. Só pode haver uma (as abas e o estado ficam nela). */
    private static MainActivity live;
    private boolean redirected;

    /** Mensagem a mostrar quando o Terminal é trazido de volta pelo vigia de aplicativos (AppGuard). */
    static final String EXTRA_WARNING = "aviso";

    /** Intent que traz o Terminal para a frente: pela tela de início, se ele for a tela de início. */
    static Intent frontIntent(Context context) {
        Intent intent = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME).setPackage(context.getPackageName());
        if (context.getPackageManager().resolveActivity(intent, PackageManager.MATCH_DEFAULT_ONLY) == null) {
            intent = new Intent(context, MainActivity.class);
        }

        return intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        showWarningFrom(intent);
    }

    private AlertDialog warningDialog;

    private void showWarningFrom(Intent intent) {
        String message = intent == null ? null : intent.getStringExtra(EXTRA_WARNING);
        if (message == null || message.trim().isEmpty() || isFinishing()) {
            return;
        }

        intent.removeExtra(EXTRA_WARNING);
        if (warningDialog != null && warningDialog.isShowing()) {
            warningDialog.dismiss();
        }

        warningDialog = new AlertDialog.Builder(this)
                .setTitle("Aplicativo não autorizado")
                .setMessage(message)
                .setPositiveButton("Entendi", null)
                .setCancelable(false)
                .show();
    }

    // ---------------------------------------------- permissões do bloqueio leve (sem cabo)

    /** Passo guiado que liga as permissões do vigia; nulo quando não está na tela. */
    private AlertDialog setupDialog;

    private boolean needsUsageAccess() {
        return !AppGuard.hasUsageAccess(this);
    }

    private boolean needsOverlay() {
        return Build.VERSION.SDK_INT >= 23 && !Settings.canDrawOverlays(this);
    }

    private ComponentName homeAlias() {
        return new ComponentName(getPackageName(), getPackageName() + ".Inicio");
    }

    /** Faz o Terminal aparecer como opção de tela de início do Android (para o passo 3 do setup). */
    private void enableHomeAlias() {
        try {
            getPackageManager().setComponentEnabledSetting(homeAlias(),
                    PackageManager.COMPONENT_ENABLED_STATE_ENABLED, PackageManager.DONT_KILL_APP);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "ativar tela de início", e);
        }
    }

    /** Tira o Terminal da lista de telas de início: o Android volta sozinho para a tela de início normal. */
    private void disableHomeAlias() {
        try {
            // DISABLED (e não DEFAULT) porque o padrão do manifesto agora é "habilitado".
            getPackageManager().setComponentEnabledSetting(homeAlias(),
                    PackageManager.COMPONENT_ENABLED_STATE_DISABLED, PackageManager.DONT_KILL_APP);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "desativar tela de início", e);
        }
    }

    private void startGuardService() {
        try {
            Intent service = new Intent(this, KeepAliveService.class);
            if (Build.VERSION.SDK_INT >= 26) {
                startForegroundService(service);
            } else {
                startService(service);
            }
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "serviço do vigia", e);
        }
    }

    /**
     * Sem cabo, o bloqueio leve depende de três ajustes que o Android só deixa ligar na mão: "Acesso aos dados
     * de uso" e "Sobrepor a outros apps" (para o vigia avisar e trazer o Terminal de volta) e deixar o Terminal
     * como "tela de início" (para o botão de início voltar sempre a ele). Enquanto faltar algum, mostra um passo
     * que abre a tela certa. Com os três, os botões de baixo deixam de levar a atendente a outro lugar. Some em
     * manutenção (o TI precisa do aparelho livre).
     */
    private void showSetupIfNeeded() {
        if (!core.isEnrolled() || isFinishing() || Core.STATE_MAINTENANCE.equals(core.state)) {
            return;
        }

        boolean usage = needsUsageAccess();
        boolean overlay = needsOverlay();
        boolean home = !terminalIsHome();
        if (!usage && !overlay && !home) {
            if (setupDialog != null && setupDialog.isShowing()) {
                setupDialog.dismiss();
            }

            setupDialog = null;
            if (Core.STATE_ACTIVE.equals(core.state)) {
                startGuardService();
            }

            return;
        }

        if (setupDialog != null && setupDialog.isShowing()) {
            return;
        }

        // Ordem fixa: 1 dados de uso, 2 sobrepor, 3 tela de início. Mostra o primeiro que falta.
        final int step = usage ? 1 : (overlay ? 2 : 3);
        String texto;
        if (step < 3) {
            String nome = step == 1 ? "Acesso aos dados de uso" : "Sobrepor a outros apps";
            texto = "Para o bloqueio funcionar neste celular, ligue três ajustes nas Configurações do Android.\n\n"
                    + "Passo " + step + " de 3: ligue \"" + nome + "\" para o Terminal Pontual.\n\n"
                    + "Toque em Abrir, ligue a chave e volte para esta tela.";
        } else {
            texto = "Passo 3 de 3: deixe o Terminal Pontual como a TELA DE INÍCIO do celular. Assim o botão de "
                    + "início volta sempre para o Terminal, e os botões de baixo não levam a atendente para fora.\n\n"
                    + "Toque em Abrir, escolha \"Terminal Pontual\" e marque \"Sempre\". Depois volte para esta tela.";
            enableHomeAlias();
        }

        setupDialog = new AlertDialog.Builder(this)
                .setTitle("Falta ligar o bloqueio")
                .setMessage(texto)
                .setCancelable(false)
                .setPositiveButton("Abrir", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        openSetupStep(step);
                    }
                })
                .setNegativeButton("Agora não", null)
                .show();
    }

    private void openSetupStep(int step) {
        Intent intent;
        if (step == 1) {
            intent = new Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS);
        } else if (step == 2) {
            intent = new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + getPackageName()));
        } else {
            intent = new Intent(Settings.ACTION_HOME_SETTINGS);
        }

        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            startActivity(intent);
        } catch (RuntimeException e) {
            // Alguns aparelhos não abrem a tela específica: cai na página do app ou nas Configurações.
            try {
                Intent fallback = step == 3
                        ? new Intent(Settings.ACTION_SETTINGS)
                        : new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName()));
                startActivity(fallback.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                toast(step == 3
                        ? "Abra Aplicativos > Escolher apps padrão > Tela de início e marque o Terminal Pontual."
                        : "Abra Aplicativos > Terminal Pontual e ligue a permissão.");
            } catch (RuntimeException ignored) {
                toast("Abra as Configurações do Android para concluir o passo.");
            }
        }
    }

    private static boolean isHomeIntent(Intent intent) {
        return intent != null && intent.hasCategory(Intent.CATEGORY_HOME);
    }

    /** O Terminal é a tela de início do aparelho (bloqueio fraco ou dono do aparelho)? */
    private boolean terminalIsHome() {
        try {
            android.content.pm.ResolveInfo ri = getPackageManager().resolveActivity(
                    new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), PackageManager.MATCH_DEFAULT_ONLY);
            return ri != null && ri.activityInfo != null && getPackageName().equals(ri.activityInfo.packageName);
        } catch (RuntimeException e) {
            return false;
        }
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        core = Core.get(this);

        // Com o Terminal como tela de início, a cópia que vale é a da tela de início. Aberto por outro
        // caminho (ícone, notificação), ele só leva até ela, em vez de criar uma segunda cópia.
        if (!isHomeIntent(getIntent()) && terminalIsHome()) {
            redirected = true;
            try {
                startActivity(new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
                        .setPackage(getPackageName()).putExtras(getIntent()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            } catch (RuntimeException e) {
                Log.w(Core.TAG, "ir para a tela de início", e);
            }

            finish();
            return;
        }

        // Sobrou uma cópia antiga (de antes de o Terminal virar a tela de início): ela é fechada.
        if (live != null && live != this && !live.isFinishing()) {
            live.finishAndRemoveTask();
        }

        live = this;
        core.listener = this;
        getWindow().setStatusBarColor(Color.WHITE);
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR);
        buildUi();
        setContentView(root);
        if (core.isEnrolled()) {
            showMain();
        } else {
            showEnroll();
        }

        showWarningFrom(getIntent());
    }

    @Override
    protected void onStart() {
        super.onStart();
        if (redirected) {
            return;
        }

        core.listener = this;
        if (away) {
            // A atendente voltou do WhatsApp para o Terminal.
            away = false;
            endExternal();
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        if (!redirected && core.isEnrolled()) {
            Lock.apply(this, hasWhatsAppItem());
            // Sem o bloqueio total (dono do aparelho), confere as permissões do vigia e orienta a ligá-las.
            if (!Lock.isOwner(this)) {
                showSetupIfNeeded();
            }
        }
    }

    @Override
    protected void onPause() {
        resumed = false;
        super.onPause();
    }

    @Override
    protected void onStop() {
        if (core.external != null) {
            away = true;
        }

        super.onStop();
    }

    @Override
    protected void onDestroy() {
        if (redirected) {
            super.onDestroy();
            return;
        }

        if (live == this) {
            live = null;
        }

        for (Tab tab : new ArrayList<>(tabs)) {
            closeTab(tab);
        }

        destroyHome();
        core.tabsInUse.clear();
        if (core.listener == this) {
            core.listener = null;
        }

        super.onDestroy();
    }

    @Override
    public void onBackPressed() {
        if (assistant != null && assistant.isOpen()) {
            assistant.hide();
            updateLayout();
            return;
        }

        if (enrollView.getVisibility() == View.VISIBLE || !core.isEnrolled()) {
            if (!Lock.isLocked(this)) {
                moveTaskToBack(true);
            }

            return;
        }

        if (active != null) {
            if (active.blockedHost != null) {
                onBlockedBack();
            } else if (active.view.canGoBack()) {
                active.view.goBack();
            } else {
                activate(null);
            }

            return;
        }

        // Na tela inicial, "voltar" não fecha o Terminal. Sem o bloqueio, só o manda para trás.
        if (!Lock.isLocked(this)) {
            moveTaskToBack(true);
        }
    }

    // ------------------------------------------------------------------ montagem da tela

    private int dp(float value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }

    private TextView label(String text, float sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(text);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        if (bold) {
            t.setTypeface(Typeface.DEFAULT_BOLD);
        }

        return t;
    }

    private Button pill(String text, int color) {
        Button b = new Button(this);
        b.setText(text);
        b.setAllCaps(false);
        b.setTextColor(Color.WHITE);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(color);
        bg.setCornerRadius(dp(26));
        b.setBackground(bg);
        b.setStateListAnimator(null);
        b.setMinHeight(dp(52));
        b.setPadding(dp(24), 0, dp(24), 0);
        return b;
    }

    private LinearLayout.LayoutParams row(int topMargin) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        p.topMargin = dp(topMargin);
        return p;
    }

    private void buildUi() {
        int match = ViewGroup.LayoutParams.MATCH_PARENT;
        int wrap = ViewGroup.LayoutParams.WRAP_CONTENT;
        root = new FrameLayout(this);
        root.setBackgroundColor(Color.WHITE);

        // --- tela principal: barra fina + conteúdo
        mainLayout = new LinearLayout(this);
        mainLayout.setOrientation(LinearLayout.VERTICAL);
        root.addView(mainLayout, new FrameLayout.LayoutParams(match, match));

        barBox = new LinearLayout(this);
        barBox.setOrientation(LinearLayout.VERTICAL);
        LinearLayout bar = new LinearLayout(this);
        bar.setOrientation(LinearLayout.HORIZONTAL);
        bar.setGravity(Gravity.CENTER_VERTICAL);
        bar.setBackgroundColor(Color.WHITE);

        TextView homeButton = label("‹ Início", 15, BLUE, true);
        homeButton.setGravity(Gravity.CENTER);
        homeButton.setPadding(dp(14), 0, dp(12), 0);
        homeButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                activate(null);
            }
        });
        bar.addView(homeButton, new LinearLayout.LayoutParams(wrap, match));

        barTitle = label("", 15, INK, true);
        barTitle.setSingleLine(true);
        barTitle.setEllipsize(TextUtils.TruncateAt.END);
        barTitle.setPadding(dp(4), 0, dp(4), 0);
        bar.addView(barTitle, new LinearLayout.LayoutParams(0, wrap, 1f));

        helpButton = label("Chamar o TI", 14, GREEN, true);
        helpButton.setGravity(Gravity.CENTER);
        helpButton.setPadding(dp(10), 0, dp(10), 0);
        helpButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                callIt();
            }
        });
        bar.addView(helpButton, new LinearLayout.LayoutParams(wrap, match));

        TextView closeButton = label("✕", 18, SOFT, false);
        closeButton.setGravity(Gravity.CENTER);
        closeButton.setContentDescription("Fechar este site");
        closeButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                if (active != null) {
                    closeTab(active);
                }
            }
        });
        bar.addView(closeButton, new LinearLayout.LayoutParams(dp(48), match));
        barBox.addView(bar, new LinearLayout.LayoutParams(match, dp(48)));

        View line = new View(this);
        line.setBackgroundColor(LINE);
        barBox.addView(line, new LinearLayout.LayoutParams(match, dp(1)));
        mainLayout.addView(barBox, new LinearLayout.LayoutParams(match, wrap));

        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(100);
        progress.setVisibility(View.GONE);
        mainLayout.addView(progress, new LinearLayout.LayoutParams(match, dp(3)));

        content = new FrameLayout(this);
        mainLayout.addView(content, new LinearLayout.LayoutParams(match, 0, 1f));

        // --- rodapé: quem está com o celular, aviso de gravação e o botão Atualizar
        View footLine = new View(this);
        footLine.setBackgroundColor(LINE);
        mainLayout.addView(footLine, new LinearLayout.LayoutParams(match, dp(1)));
        footer = new LinearLayout(this);
        footer.setOrientation(LinearLayout.HORIZONTAL);
        footer.setGravity(Gravity.CENTER_VERTICAL);
        footer.setBackgroundColor(Color.WHITE);
        footer.setPadding(dp(12), 0, dp(4), 0);
        footerWho = label("", 13, INK, true);
        footerWho.setSingleLine(true);
        footerWho.setEllipsize(TextUtils.TruncateAt.END);
        footerWho.setGravity(Gravity.CENTER_VERTICAL);
        footerWho.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                switchAttendant();
            }
        });
        footer.addView(footerWho, new LinearLayout.LayoutParams(0, match, 1f));
        footerRec = label("● Tela gravada", 12, RUBY, true);
        footerRec.setPadding(dp(8), 0, dp(8), 0);
        footerRec.setGravity(Gravity.CENTER_VERTICAL);
        footer.addView(footerRec, new LinearLayout.LayoutParams(wrap, match));
        footerUpdate = label("Atualizar", 13, BLUE, true);
        footerUpdate.setPadding(dp(10), 0, dp(10), 0);
        footerUpdate.setGravity(Gravity.CENTER_VERTICAL);
        footerUpdate.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                startUpdate();
            }
        });
        footer.addView(footerUpdate, new LinearLayout.LayoutParams(wrap, match));
        mainLayout.addView(footer, new LinearLayout.LayoutParams(match, dp(40)));

        bubble = label("IA", 18, Color.WHITE, true);
        bubble.setGravity(Gravity.CENTER);
        GradientDrawable round = new GradientDrawable();
        round.setShape(GradientDrawable.OVAL);
        round.setColor(BLUE);
        bubble.setBackground(round);
        bubble.setElevation(dp(6));
        bubble.setContentDescription("Assistente de IA");
        bubble.setVisibility(View.GONE);
        bubble.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                openAssistant();
            }
        });
        FrameLayout.LayoutParams bp = new FrameLayout.LayoutParams(dp(56), dp(56), Gravity.BOTTOM | Gravity.END);
        bp.setMargins(0, 0, dp(16), dp(56));
        root.addView(bubble, bp);

        // --- aviso de site não permitido
        blockedPanel = new LinearLayout(this);
        blockedPanel.setOrientation(LinearLayout.VERTICAL);
        blockedPanel.setGravity(Gravity.CENTER);
        blockedPanel.setBackgroundColor(Color.WHITE);
        blockedPanel.setPadding(dp(28), dp(28), dp(28), dp(28));
        blockedPanel.setClickable(true);
        TextView blockedTitle = label("Site não permitido", 24, RUBY, true);
        blockedTitle.setGravity(Gravity.CENTER);
        blockedPanel.addView(blockedTitle, row(0));
        blockedText = label("", 16, SOFT, false);
        blockedText.setGravity(Gravity.CENTER);
        blockedPanel.addView(blockedText, row(12));
        blockedBack = pill("Voltar", BLUE);
        blockedBack.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                onBlockedBack();
            }
        });
        LinearLayout.LayoutParams backParams = new LinearLayout.LayoutParams(wrap, wrap);
        backParams.topMargin = dp(24);
        blockedPanel.addView(blockedBack, backParams);
        blockedPanel.setVisibility(View.GONE);
        content.addView(blockedPanel, new FrameLayout.LayoutParams(match, match));

        // --- aviso de tela cheia (sem conexão com o servidor)
        overlay = new LinearLayout(this);
        overlay.setOrientation(LinearLayout.VERTICAL);
        overlay.setGravity(Gravity.CENTER);
        overlay.setBackgroundColor(Color.WHITE);
        overlay.setPadding(dp(28), dp(28), dp(28), dp(28));
        overlay.setClickable(true);
        TextView overlayTitle = label("Sem conexão com o servidor", 24, INK, true);
        overlayTitle.setGravity(Gravity.CENTER);
        overlayTitle.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                countTap();
            }
        });
        overlay.addView(overlayTitle, row(0));
        overlayText = label("", 16, SOFT, false);
        overlayText.setGravity(Gravity.CENTER);
        overlay.addView(overlayText, row(12));
        overlay.setVisibility(View.GONE);
        root.addView(overlay, new FrameLayout.LayoutParams(match, match));

        // --- primeira configuração
        enrollView = new ScrollView(this);
        enrollView.setFillViewport(true);
        enrollView.setBackgroundColor(Color.WHITE);
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(24), dp(40), dp(24), dp(24));
        TextView title = label("Terminal Pontual", 28, BLUE, true);
        title.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                countTap();
            }
        });
        form.addView(title, row(0));
        form.addView(label("Primeira configuração deste aparelho", 16, SOFT, false), row(4));

        form.addView(label("Endereço da Central do TI", 14, INK, true), row(28));
        enrollServer = new EditText(this);
        enrollServer.setSingleLine(true);
        enrollServer.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        enrollServer.setHint(EXAMPLE_SERVER);
        form.addView(enrollServer, row(4));

        form.addView(label("Código de cadastro", 14, INK, true), row(16));
        enrollCode = new EditText(this);
        enrollCode.setSingleLine(true);
        enrollCode.setHint("XXXX-XXXX");
        enrollCode.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
                | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        form.addView(enrollCode, row(4));
        form.addView(label("Na Central do TI, clique em \"Cadastrar nova máquina\" para gerar o código. "
                + "Ele vale por 30 minutos.", 13, SOFT, false), row(4));

        enrollButton = pill("Conectar", BLUE);
        enrollButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                enroll();
            }
        });
        form.addView(enrollButton, row(24));

        enrollError = label("", 14, RUBY, false);
        form.addView(enrollError, row(12));

        form.addView(label("O uso deste aparelho é registrado pela Pontual: horários em que está ligado e "
                + "conectado, quais aplicativos e sites autorizados foram abertos, tentativas de abrir site ou "
                + "aplicativo não permitido e chamados ao TI. Quando a Central liga, a tela também é gravada em vídeo "
                + "(em horário comercial), guardada protegida e apagada depois de 3 meses; o Terminal avisa enquanto grava. "
                + "Não são registrados o que você digita, áudio, câmera ou localização.", 13, SOFT, false), row(28));
        enrollView.addView(form, new ScrollView.LayoutParams(match, wrap));
        root.addView(enrollView, new FrameLayout.LayoutParams(match, match));
    }

    // ------------------------------------------------------------------ primeira configuração

    private void showEnroll() {
        if (assistant != null) {
            assistant.destroy();
        }

        bubble.setVisibility(View.GONE);
        for (Tab tab : new ArrayList<>(tabs)) {
            closeTab(tab);
        }

        destroyHome();
        active = null;
        homeFailed = false;
        mainLayout.setVisibility(View.GONE);
        overlay.setVisibility(View.GONE);
        enrollView.setVisibility(View.VISIBLE);
        enrollError.setText("");
        enrollButton.setEnabled(true);
        enrollButton.setText("Conectar");
        // Para o teste pelo cabo: "am start ... --es servidor http://localhost:5080" já preenche o endereço.
        String suggested = getIntent() == null ? null : getIntent().getStringExtra("servidor");
        if (suggested != null && normalizeServer(suggested) != null) {
            enrollServer.setText(normalizeServer(suggested));
        }

        // Sem cadastro não há bloqueio: o TI precisa do aparelho livre para configurar.
        Lock.release(this);
        // No bloqueio leve, devolve a tela de início normal do Android (o dono do aparelho é tratado pelo Lock).
        if (!Lock.isOwner(this)) {
            disableHomeAlias();
        }

        stopService(new Intent(this, KeepAliveService.class));
    }

    /** Deixa o endereço no formato que o WebView usa: esquema e domínio em minúsculas, sem porta padrão nem barra final. */
    private static String normalizeServer(String raw) {
        String text = raw == null ? "" : raw.trim();
        if (text.isEmpty()) {
            return null;
        }

        if (!text.contains("://")) {
            text = "http://" + text;
        }

        try {
            URL u = new URL(text);
            String scheme = u.getProtocol().toLowerCase(Locale.ROOT);
            String host = u.getHost() == null ? "" : u.getHost().toLowerCase(Locale.ROOT);
            if ((!scheme.equals("http") && !scheme.equals("https")) || host.isEmpty() || u.getUserInfo() != null) {
                return null;
            }

            int port = u.getPort();
            return scheme + "://" + host + (port == -1 || port == u.getDefaultPort() ? "" : ":" + port);
        } catch (MalformedURLException e) {
            return null;
        }
    }

    private void enroll() {
        final String url = normalizeServer(enrollServer.getText().toString());
        final String code = enrollCode.getText().toString().trim();
        if (url == null) {
            enrollError.setText("Informe o endereço da Central do TI. Ele aparece na janela preta da Central. Exemplo: " + EXAMPLE_SERVER);
            return;
        }

        if (code.isEmpty()) {
            enrollError.setText("Informe o código de cadastro gerado na Central do TI.");
            return;
        }

        enrollError.setText("");
        enrollButton.setEnabled(false);
        enrollButton.setText("Conectando…");
        final String hostname = core.hostname();
        new Thread(new Runnable() {
            @Override
            public void run() {
                String token = "";
                String error = null;
                try {
                    JSONObject body = new JSONObject();
                    body.put("code", code);
                    body.put("hostname", hostname);
                    body.put("windows", Core.systemLabel());
                    token = Core.str(Api.call("POST", url, null, "/device/enroll", body), "token");
                    if (token.isEmpty()) {
                        error = "O servidor respondeu ao cadastro sem o token.";
                    }
                } catch (Api.HttpError e) {
                    error = e.getMessage();
                } catch (JSONException e) {
                    error = "O servidor mandou uma resposta que o Terminal não entendeu.";
                } catch (IOException e) {
                    error = "Não foi possível falar com o servidor em " + url + ". Confira o endereço, se a "
                            + "Central do TI está aberta e se o celular está no Wi-Fi da empresa.";
                }

                final String finalToken = token;
                final String finalError = error;
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        if (isDestroyed()) {
                            return;
                        }

                        if (finalError != null) {
                            enrollError.setText(finalError);
                            enrollButton.setEnabled(true);
                            enrollButton.setText("Conectar");
                            return;
                        }

                        core.saveEnrollment(url, finalToken);
                        enrollCode.setText("");
                        showMain();
                        if (resumed) {
                            Lock.apply(MainActivity.this, hasWhatsAppItem());
                        }
                    }
                });
            }
        }).start();
    }

    // ------------------------------------------------------------------ tela inicial (a página da Central)

    private void showMain() {
        enrollView.setVisibility(View.GONE);
        mainLayout.setVisibility(View.VISIBLE);
        if (home == null) {
            createHome();
        }

        homeReady = false;
        homeFailed = false;
        home.loadUrl(homeUrl());
        core.start();
        try {
            Intent service = new Intent(this, KeepAliveService.class);
            if (Build.VERSION.SDK_INT >= 26) {
                startForegroundService(service);
            } else {
                startService(service);
            }
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "iniciar serviço", e);
        }

        if (Build.VERSION.SDK_INT >= 33 && !askedNotifications && !Lock.isLocked(this)
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            askedNotifications = true;
            requestPermissions(new String[] {Manifest.permission.POST_NOTIFICATIONS}, 1);
        }

        if (!Lock.isOwner(this)) {
            showSetupIfNeeded();
        }

        updateLayout();
    }

    private String homeUrl() {
        return core.serverUrl + "/terminal/";
    }

    private boolean isHomeUrl(String url) {
        String base = homeUrl();
        return url != null && core.isEnrolled() && url.regionMatches(true, 0, base, 0, base.length());
    }

    private void createHome() {
        home = new WebView(this);
        WebSettings s = home.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setSupportMultipleWindows(false);
        s.setGeolocationEnabled(false);
        s.setSaveFormData(false);
        s.setTextZoom(100);
        home.setOverScrollMode(View.OVER_SCROLL_NEVER);
        // A ponte só existe neste WebView, que nunca sai do endereço da tela inicial.
        home.addJavascriptInterface(new Bridge(), "PontualAndroid");
        home.setWebViewClient(new HomeClient());
        home.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onConsoleMessage(ConsoleMessage m) {
                Log.i(Core.TAG, "página: " + m.message() + " (linha " + m.lineNumber() + ")");
                return true;
            }
        });
        content.addView(home, 0, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
    }

    private void destroyHome() {
        if (home == null) {
            return;
        }

        WebView old = home;
        home = null;
        homeReady = false;
        content.removeView(old);
        old.stopLoading();
        old.destroy();
    }

    /**
     * O que vai na frente do app.js da tela inicial: o objeto window.PONTUAL (seção 6 do contrato), uma ponte
     * com a mesma forma da que o WebView2 oferece no PC (window.chrome.webview) e os ajustes de tela pequena.
     * Assim a página da Central é a mesma para o PC e para o celular.
     */
    private String prelude() {
        JSONObject info = new JSONObject();
        try {
            info.put("token", core.token);
            info.put("hostname", core.hostname());
            info.put("hosted", true);
        } catch (JSONException e) {
            Log.w(Core.TAG, "ponte", e);
        }

        return "(function(){if(window.top!==window)return;"
                + "var L=[];"
                + "window.__pontualReceber=function(m){for(var i=0;i<L.length;i++){try{L[i]({data:m});}catch(e){}}};"
                + "var wv={postMessage:function(m){try{PontualAndroid.post(JSON.stringify(m));}catch(e){}},"
                + "addEventListener:function(t,f){if(t==='message')L.push(f);},removeEventListener:function(){}};"
                + "if(!window.chrome)window.chrome={};"
                + "window.chrome.webview=wv;"
                + "Object.defineProperty(window,'PONTUAL',{value:Object.freeze(" + info + "),writable:false,configurable:false});"
                + "var st=document.createElement('style');st.textContent=" + JSONObject.quote(PHONE_CSS) + ";"
                + "(document.head||document.documentElement).appendChild(st);"
                // Sete toques seguidos na logo abrem o menu de teste do TI.
                + "var n=0,t0=0;document.addEventListener('click',function(e){"
                + "if(!(e.target&&e.target.closest&&e.target.closest('.logo')))return;"
                + "var now=Date.now();if(now-t0>4000){n=0;t0=now;}n++;"
                + "if(n>=7){n=0;wv.postMessage({type:'menu-teste'});}},true);"
                + "})();\n";
    }

    private final class Bridge {
        @JavascriptInterface
        public void post(final String json) {
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    onHomeMessage(json);
                }
            });
        }
    }

    private final class HomeClient extends WebViewClient {
        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            // A tela inicial nunca sai do próprio endereço.
            return !isHomeUrl(request.getUrl().toString());
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
            Uri u = request.getUrl();
            if (request.isForMainFrame() || !"GET".equals(request.getMethod())
                    || !"/terminal/app.js".equals(u.getPath()) || !isHomeUrl(u.toString())) {
                return null;
            }

            byte[] script;
            try {
                byte[] original = Api.fetch(core.serverUrl + "/terminal/app.js");
                int skip = original.length >= 3 && (original[0] & 0xFF) == 0xEF && (original[1] & 0xFF) == 0xBB
                        && (original[2] & 0xFF) == 0xBF ? 3 : 0;
                byte[] before = prelude().getBytes(StandardCharsets.UTF_8);
                script = new byte[before.length + original.length - skip];
                System.arraycopy(before, 0, script, 0, before.length);
                System.arraycopy(original, skip, script, before.length, original.length - skip);
            } catch (IOException e) {
                // Sem o app.js a página não funciona: vale como falha de carga, e o ciclo tenta de novo.
                homeLoadFailed = true;
                script = new byte[0];
            }

            WebResourceResponse response = new WebResourceResponse("application/javascript", "utf-8",
                    new ByteArrayInputStream(script));
            Map<String, String> headers = new HashMap<>();
            headers.put("Cache-Control", "no-store");
            response.setResponseHeaders(headers);
            return response;
        }

        @Override
        public void onPageStarted(WebView view, String url, Bitmap favicon) {
            homeLoadFailed = false;
            homeReady = false;
        }

        @Override
        public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
            if (request.isForMainFrame()) {
                homeLoadFailed = true;
            }
        }

        @Override
        public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
            if (request.isForMainFrame() && response.getStatusCode() >= 400) {
                homeLoadFailed = true;
            }
        }

        @Override
        public void onPageFinished(WebView view, String url) {
            homeFailed = homeLoadFailed;
            updateLayout();
        }

        @Override
        public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
            // O navegador interno caiu: refaz a tela inicial em vez de deixar o app fechar.
            view.post(new Runnable() {
                @Override
                public void run() {
                    if (isDestroyed() || !core.isEnrolled()) {
                        return;
                    }

                    destroyHome();
                    createHome();
                    home.loadUrl(homeUrl());
                    updateLayout();
                }
            });
            return true;
        }
    }

    private void onHomeMessage(String json) {
        if (home == null || !isHomeUrl(home.getUrl())) {
            return;
        }

        try {
            JSONObject m = new JSONObject(json);
            String type = Core.str(m, "type");
            String id = Core.str(m, "id");
            if (type.equals("ready")) {
                homeReady = true;
                sendState();
                sendStatus();
            } else if (type.equals("open") && !id.isEmpty()) {
                openItem(id);
            } else if (type.equals("focus") && !id.isEmpty()) {
                focusItem(id);
            } else if (type.equals("close") && !id.isEmpty()) {
                closeItem(id);
            } else if (type.equals("help")) {
                core.record("help", null, null);
            } else if (type.equals("menu-teste")) {
                showTestMenu();
            }
        } catch (JSONException e) {
            Log.w(Core.TAG, "mensagem da tela inicial", e);
        }
    }

    private void postToHome(JSONObject message) {
        if (!homeReady || home == null) {
            return;
        }

        home.evaluateJavascript("window.__pontualReceber&&window.__pontualReceber(" + message + ");", null);
    }

    /** Mensagem "state" do contrato: o que está aberto agora (sites e o WhatsApp). */
    private void sendState() {
        try {
            JSONArray open = new JSONArray();
            for (Tab tab : tabs) {
                open.put(new JSONObject().put("id", tab.item.id).put("name", tab.item.name));
            }

            if (core.external != null) {
                open.put(new JSONObject().put("id", core.external.id).put("name", core.external.name));
            }

            postToHome(new JSONObject().put("type", "state").put("open", open));
        } catch (JSONException e) {
            Log.w(Core.TAG, "state", e);
        }
    }

    /** Mensagem "status": estado do aparelho (pendente, manutenção) e se há conexão. */
    private void sendStatus() {
        if (core.online == null) {
            return;
        }

        try {
            JSONObject m = new JSONObject();
            m.put("type", "status");
            m.put("state", core.state);
            m.put("online", core.online.booleanValue());
            m.put("attendantName", core.attendantName);
            m.put("maintenance", Core.STATE_MAINTENANCE.equals(core.state)
                    ? new JSONObject().put("reason", core.maintenanceReason).put("until", core.maintenanceUntil)
                    : JSONObject.NULL);
            postToHome(m);
        } catch (JSONException e) {
            Log.w(Core.TAG, "status", e);
        }
    }

    // ------------------------------------------------------------------ avisos do ciclo com o servidor

    @Override
    public void onStatus() {
        if (isDestroyed() || !core.isEnrolled()) {
            return;
        }

        if (homeFailed && Boolean.TRUE.equals(core.online) && home != null) {
            home.loadUrl(homeUrl());
        }

        if (Core.STATE_PENDING.equals(core.state)) {
            for (Tab tab : new ArrayList<>(tabs)) {
                closeTab(tab);
            }
        } else if (Core.STATE_MAINTENANCE.equals(core.state) && active != null) {
            // Em manutenção a tela inicial mostra o aviso de tela cheia.
            activate(null);
        }

        sendStatus();
        if (!Lock.isOwner(this)) {
            showSetupIfNeeded();
        }

        maybePickAttendant();
        maybeAskRecording();
        updateLayout();
    }

    @Override
    public void onConfig() {
        if (isDestroyed()) {
            return;
        }

        // O que deixou de ser permitido é fechado.
        for (Tab tab : new ArrayList<>(tabs)) {
            if (core.find(tab.item.id) == null) {
                closeTab(tab);
            }
        }

        if (core.external != null && core.find(core.external.id) == null) {
            endExternal();
        }

        try {
            postToHome(new JSONObject().put("type", "config"));
        } catch (JSONException e) {
            Log.w(Core.TAG, "config", e);
        }

        // A lista do que o bloqueio deixa abrir acompanha a Central (o WhatsApp entra ou sai).
        if (core.isEnrolled()) {
            Lock.apply(this, hasWhatsAppItem());
        }
    }

    @Override
    public void onUnenrolled() {
        if (isDestroyed()) {
            return;
        }

        showEnroll();
        toast("Este aparelho foi removido na Central do TI. Faça o cadastro de novo.");
    }

    // ------------------------------------------------------------------ abrir, focar e fechar

    private void openItem(String id) {
        Core.Item item = core.find(id);
        if (item == null || !Core.STATE_ACTIVE.equals(core.state)) {
            return;
        }

        if (isWhatsApp(item)) {
            openWhatsApp(item);
            return;
        }

        if (item.isProgram()) {
            toast(item.name + " é um programa de computador e não abre no celular.");
            return;
        }

        for (Tab tab : tabs) {
            if (tab.item.id.equals(id)) {
                activate(tab);
                return;
            }
        }

        // O próprio endereço do item passa pela mesma política das navegações.
        if (core.policy.evaluate(item.target).verdict == HostPolicy.SCHEME_BLOCKED) {
            toast("O endereço de " + item.name + " está incorreto. Chame o TI.");
            return;
        }

        openSite(item);
    }

    private void openSite(Core.Item item) {
        WebView view = new WebView(this);
        final Tab tab = new Tab(item, view);
        WebSettings s = view.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setDatabaseEnabled(true);
        // Sem janelas novas: o destino abre na mesma aba e passa pela mesma política.
        s.setSupportMultipleWindows(false);
        s.setJavaScriptCanOpenWindowsAutomatically(false);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setGeolocationEnabled(false);
        s.setSaveFormData(false);
        // Os portais foram feitos para monitor: a página abre inteira e a atendente aproxima com os dedos.
        s.setUseWideViewPort(true);
        s.setLoadWithOverviewMode(true);
        s.setSupportZoom(true);
        s.setBuiltInZoomControls(true);
        s.setDisplayZoomControls(false);
        CookieManager.getInstance().setAcceptCookie(true);
        CookieManager.getInstance().setAcceptThirdPartyCookies(view, true);
        view.setWebViewClient(new SiteClient(tab));
        view.setWebChromeClient(new SiteChrome(tab));
        view.setDownloadListener(new DownloadListener() {
            @Override
            public void onDownloadStart(String url, String userAgent, String disposition, String mime, long length) {
                toast("Downloads não são permitidos neste aparelho.");
            }
        });

        tabs.add(tab);
        content.addView(view, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        // O aviso de site não permitido fica sempre por cima dos sites.
        blockedPanel.bringToFront();
        core.record("opened", item.name, null);
        syncInUse();
        sendState();
        activate(tab);
        view.loadUrl(item.target);
    }

    /**
     * Política de navegação da página principal da aba. Devolve true quando a navegação não pode seguir.
     * Chamada duas vezes por navegação (antes de começar e quando a página começa a carregar), porque o
     * Android não avisa a primeira vez em todos os casos (formulários enviados por POST, por exemplo).
     */
    private boolean refuses(Tab tab, String url) {
        HostPolicy.Decision d = core.policy.evaluate(url);
        if (d.verdict == HostPolicy.ALLOWED) {
            return false;
        }

        if (d.verdict == HostPolicy.SCHEME_BLOCKED) {
            // intent:, tel:, mailto:, market: e afins abririam outros aplicativos do aparelho.
            return true;
        }

        long now = SystemClock.elapsedRealtime();
        if (core.observationMode) {
            // Modo de observação: deixa passar e registra o domínio para o TI avaliar.
            if (!d.host.equals(tab.lastObservedHost) || now - tab.lastObservedAt > 10000) {
                core.record("observed", tab.item.name, d.host);
            }

            tab.lastObservedHost = d.host;
            tab.lastObservedAt = now;
            return false;
        }

        if (!d.host.equals(tab.lastBlockedHost) || now - tab.lastBlockedAt > 10000) {
            core.record("blocked", tab.item.name, d.host);
        }

        tab.lastBlockedHost = d.host;
        tab.lastBlockedAt = now;
        tab.blockedHost = d.host;
        updateLayout();
        return true;
    }

    private final class SiteClient extends WebViewClient {
        private final Tab tab;

        SiteClient(Tab tab) {
            this.tab = tab;
        }

        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            if (tab.closed) {
                return true;
            }

            // A regra vale para a página principal; quadros internos seguem o que o próprio site carrega.
            return request.isForMainFrame() && refuses(tab, request.getUrl().toString());
        }

        @Override
        public void onPageStarted(WebView view, String url, Bitmap favicon) {
            if (!tab.closed && refuses(tab, url)) {
                view.stopLoading();
            }
        }

        @Override
        public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
            view.post(new Runnable() {
                @Override
                public void run() {
                    if (!isDestroyed() && !tab.closed) {
                        closeTab(tab);
                        toast(tab.item.name + " parou de funcionar e foi fechado. Abra de novo.");
                    }
                }
            });
            return true;
        }
    }

    private final class SiteChrome extends WebChromeClient {
        private final Tab tab;

        SiteChrome(Tab tab) {
            this.tab = tab;
        }

        @Override
        public void onProgressChanged(WebView view, int value) {
            if (tab == active) {
                progress.setProgress(value);
                progress.setVisibility(value < 100 ? View.VISIBLE : View.GONE);
            }
        }

        @Override
        public boolean onShowFileChooser(WebView view, ValueCallback<Uri[]> callback, FileChooserParams params) {
            toast("Enviar arquivos do aparelho ainda não é permitido nesta versão de teste.");
            return false;
        }

        @Override
        public void onPermissionRequest(PermissionRequest request) {
            // Câmera e microfone não são liberados para os sites.
            request.deny();
        }

        @Override
        public void onGeolocationPermissionsShowPrompt(String origin, GeolocationPermissions.Callback callback) {
            callback.invoke(origin, false, false);
        }
    }

    private void focusItem(String id) {
        for (Tab tab : tabs) {
            if (tab.item.id.equals(id)) {
                activate(tab);
                return;
            }
        }

        if (core.external != null && core.external.id.equals(id)) {
            openWhatsApp(core.external);
        }
    }

    private void closeItem(String id) {
        for (Tab tab : tabs) {
            if (tab.item.id.equals(id)) {
                closeTab(tab);
                return;
            }
        }

        if (core.external != null && core.external.id.equals(id)) {
            endExternal();
        }
    }

    private void activate(Tab tab) {
        active = tab;
        updateLayout();
    }

    private void closeTab(Tab tab) {
        if (tab.closed) {
            return;
        }

        tab.closed = true;
        tabs.remove(tab);
        content.removeView(tab.view);
        tab.view.stopLoading();
        tab.view.destroy();
        core.record("closed", tab.item.name, null);
        syncInUse();
        sendState();
        if (active == tab) {
            activate(null);
        }
    }

    private void syncInUse() {
        core.tabsInUse.clear();
        for (Tab tab : tabs) {
            core.tabsInUse.add(tab.item.name);
        }
    }

    private void onBlockedBack() {
        if (active == null) {
            return;
        }

        Tab tab = active;
        tab.blockedHost = null;
        String url = tab.view.getUrl();
        if (url != null && (url.startsWith("http://") || url.startsWith("https://"))) {
            // A navegação foi cancelada: a página em que a atendente estava continua lá.
            updateLayout();
        } else {
            // O bloqueio foi logo na entrada: não há página para onde voltar.
            closeTab(tab);
        }
    }

    // ------------------------------------------------------------------ WhatsApp (aplicativo do aparelho)

    /** No celular, o item do WhatsApp Web (domínio whatsapp.com) abre o aplicativo instalado. */
    static boolean isWhatsApp(Core.Item item) {
        if (item.isProgram()) {
            return false;
        }

        List<String> hosts = new ArrayList<>(item.hosts);
        String target = HostPolicy.normalizeHost(item.target);
        if (target != null) {
            hosts.add(target);
        }

        for (String raw : hosts) {
            String h = HostPolicy.normalizeHost(raw);
            if (h != null && (h.equals("whatsapp.com") || h.endsWith(".whatsapp.com"))) {
                return true;
            }
        }

        return false;
    }

    private boolean hasWhatsAppItem() {
        for (Core.Item item : core.items) {
            if (isWhatsApp(item)) {
                return true;
            }
        }

        return false;
    }

    private void openWhatsApp(final Core.Item item) {
        // Todos: WhatsApp, WhatsApp Business e os duplicados (Dual Messenger), cada um com o seu número.
        final List<Lock.App> apps = Lock.whatsAppTargets(this);
        if (apps.isEmpty()) {
            toast("O WhatsApp não está instalado neste aparelho. Chame o TI.");
            return;
        }

        if (apps.size() == 1) {
            startExternal(item, apps.get(0));
            return;
        }

        String[] names = new String[apps.size()];
        for (int i = 0; i < names.length; i++) {
            names[i] = apps.get(i).label;
        }

        new AlertDialog.Builder(this)
                .setTitle("Qual WhatsApp abrir?")
                .setItems(names, new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        startExternal(item, apps.get(which));
                    }
                })
                .setNegativeButton("Cancelar", null)
                .show();
    }

    private void startExternal(Core.Item item, Lock.App app) {
        if (!Lock.launch(this, app)) {
            toast("Não foi possível abrir o " + app.label + ". Chame o TI.");
            return;
        }

        if (core.external == null || !core.external.id.equals(item.id)) {
            core.external = item;
            core.record("opened", item.name, null);
            sendState();
        }
    }

    /** O WhatsApp deixa de contar como "em uso": a atendente voltou ao Terminal, ou o item foi fechado ou retirado. */
    private void endExternal() {
        if (core.external == null) {
            return;
        }

        core.record("closed", core.external.name, null);
        core.external = null;
        sendState();
    }

    // ------------------------------------------------------------------ o que aparece na tela

    private void updateLayout() {
        if (!core.isEnrolled()) {
            return;
        }

        overlay.setVisibility(homeFailed ? View.VISIBLE : View.GONE);
        if (homeFailed) {
            overlayText.setText("O Terminal não conseguiu abrir a tela inicial em " + core.serverUrl
                    + ". Ele tenta de novo sozinho a cada 15 segundos.");
        }

        barBox.setVisibility(active != null ? View.VISIBLE : View.GONE);
        boolean blocked = active != null && active.blockedHost != null;
        if (active != null) {
            barTitle.setText(active.item.name);
        } else {
            progress.setVisibility(View.GONE);
        }

        if (blocked) {
            String url = active.view.getUrl();
            boolean hasPage = url != null && (url.startsWith("http://") || url.startsWith("https://"));
            blockedText.setText("Só os sites autorizados pela Pontual abrem neste aparelho. A tentativa de abrir "
                    + active.blockedHost + " foi registrada e o TI foi avisado.");
            blockedBack.setText(hasPage ? "Voltar para " + active.item.name : "Voltar ao início");
        }

        blockedPanel.setVisibility(blocked ? View.VISIBLE : View.GONE);
        String who = core.attendantName == null ? "" : core.attendantName.trim();
        footerWho.setText(who.isEmpty() ? "Toque aqui e escolha seu nome" : "Com " + who + "  ·  Trocar");
        footerRec.setVisibility(RecorderService.recording ? View.VISIBLE : View.GONE);
        boolean showBubble = core.assistant && Core.STATE_ACTIVE.equals(core.state) && !homeFailed
                && !who.isEmpty() && (assistant == null || !assistant.isOpen());
        bubble.setVisibility(showBubble ? View.VISIBLE : View.GONE);
        if (!core.assistant && assistant != null) {
            assistant.destroy();
        }
        if (home != null) {
            home.setVisibility(active == null ? View.VISIBLE : View.GONE);
        }

        for (Tab tab : tabs) {
            tab.view.setVisibility(tab == active && !blocked ? View.VISIBLE : View.GONE);
        }
    }

    private void toast(String message) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
    }

    private void callIt() {
        core.record("help", null, null);
        toast(Boolean.FALSE.equals(core.online)
                ? "Sem conexão agora. O chamado vai ser enviado ao TI assim que a conexão voltar."
                : "Chamado enviado. O TI já foi avisado.");
        // Pausa curta para um toque repetido não virar vários chamados.
        helpButton.setEnabled(false);
        helpButton.setAlpha(0.45f);
        helpButton.postDelayed(new Runnable() {
            @Override
            public void run() {
                helpButton.setEnabled(true);
                helpButton.setAlpha(1f);
            }
        }, 8000);
    }

    // ------------------------------------------------------------------ menu de teste (só para o TI)

    private void countTap() {
        long now = SystemClock.elapsedRealtime();
        if (now - firstTapAt > 4000) {
            taps = 0;
            firstTapAt = now;
        }

        if (++taps >= 7) {
            taps = 0;
            showTestMenu();
        }
    }

    /**
     * SAÍDA SÓ PARA O TESTE, como o Ctrl+Shift+Alt+Q do PC: sete toques na logo (ou no título das telas de
     * cadastro e de falta de conexão). Na versão de produção isto sai, ou passa a pedir a senha do TI.
     */
    private void showTestMenu() {
        final List<String> options = new ArrayList<>();
        final boolean owner = Lock.isOwner(this);
        if (core.isEnrolled()) {
            options.add("Refazer o cadastro deste aparelho");
        }

        if (owner) {
            options.add("Remover o bloqueio deste aparelho");
        }

        if (options.isEmpty()) {
            toast("Menu de teste: nada a fazer aqui.");
            return;
        }

        new AlertDialog.Builder(this)
                .setTitle("Menu de teste (só para o TI)")
                .setItems(options.toArray(new String[0]), new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        if (options.get(which).startsWith("Refazer")) {
                            confirm("Refazer o cadastro? O aparelho sai da Central até ser cadastrado de novo.",
                                    new Runnable() {
                                        @Override
                                        public void run() {
                                            core.clearEnrollment();
                                            showEnroll();
                                        }
                                    });
                        } else {
                            confirm("Remover o bloqueio? O aparelho volta a ser um celular comum. Para bloquear "
                                    + "de novo será preciso o cabo USB.", new Runnable() {
                                        @Override
                                        public void run() {
                                            toast(Lock.remove(MainActivity.this)
                                                    ? "Bloqueio removido. Este aparelho voltou a ser um celular comum."
                                                    : "Não deu para remover por aqui. Use o desbloquear.ps1 pelo cabo USB.");
                                        }
                                    });
                        }
                    }
                })
                .setNegativeButton("Cancelar", null)
                .show();
    }

    private void confirm(String question, final Runnable action) {
        new AlertDialog.Builder(this)
                .setMessage(question)
                .setPositiveButton("Sim", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        action.run();
                    }
                })
                .setNegativeButton("Não", null)
                .show();
    }

    // ------------------------------------------------------------------ celular compartilhado (solução 2)
    // Quem pega o celular toca no próprio nome; ao devolver, em "Trocar". Sem senha (decisão do dono, 23/11/2026).

    private void maybePickAttendant() {
        if (picking || !resumed || !core.isEnrolled() || !Core.STATE_ACTIVE.equals(core.state)
                || !Boolean.TRUE.equals(core.online) || !core.attendantName.trim().isEmpty()) {
            return;
        }

        pickAttendant();
    }

    private void switchAttendant() {
        if (picking) {
            return;
        }

        String who = core.attendantName.trim();
        if (who.isEmpty()) {
            pickAttendant();
            return;
        }

        confirm(who + " vai devolver o celular? Os sites abertos são fechados e as contas entradas neles saem.", new Runnable() {
            @Override
            public void run() {
                setAttendant("", true);
            }
        });
    }

    private void pickAttendant() {
        picking = true;
        new Thread(new Runnable() {
            @Override
            public void run() {
                final List<String> names = new ArrayList<>();
                try {
                    JSONObject o = Api.call("GET", core.serverUrl, core.token, "/device/attendants", null);
                    JSONArray a = o == null ? null : o.optJSONArray("names");
                    for (int i = 0; a != null && i < a.length(); i++) {
                        names.add(a.optString(i));
                    }
                } catch (Exception e) {
                    Log.w(Core.TAG, "nomes das atendentes", e);
                }

                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        showNames(names);
                    }
                });
            }
        }).start();
    }

    private void showNames(final List<String> names) {
        if (isDestroyed()) {
            picking = false;
            return;
        }

        final List<String> options = new ArrayList<>(names);
        options.add("Meu nome não está aqui…");
        new AlertDialog.Builder(this)
                .setTitle("Quem está usando este celular?")
                .setCancelable(false)
                .setItems(options.toArray(new String[0]), new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        if (which < names.size()) {
                            setAttendant(names.get(which), false);
                        } else {
                            typeName();
                        }
                    }
                })
                .show();
    }

    private void typeName() {
        final EditText field = new EditText(this);
        field.setSingleLine(true);
        field.setHint("Seu nome");
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_WORDS);
        new AlertDialog.Builder(this)
                .setTitle("Digite seu nome")
                .setView(field)
                .setCancelable(false)
                .setPositiveButton("Entrar", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        String name = field.getText().toString().trim();
                        if (name.length() < 2) {
                            picking = false;
                            pickAttendant();
                        } else {
                            setAttendant(name.length() > 80 ? name.substring(0, 80) : name, false);
                        }
                    }
                })
                .setNegativeButton("Voltar", new DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(DialogInterface dialog, int which) {
                        picking = false;
                        pickAttendant();
                    }
                })
                .show();
    }

    /** Avisa a Central e limpa o que era da atendente anterior: sites, contas entradas e a conversa com a IA. */
    private void setAttendant(final String name, final boolean thenPick) {
        picking = true;
        for (Tab tab : new ArrayList<>(tabs)) {
            closeTab(tab);
        }

        activate(null);
        CookieManager.getInstance().removeAllCookies(null);
        WebStorage.getInstance().deleteAllData();
        if (assistant != null) {
            assistant.destroy();
        }

        new Thread(new Runnable() {
            @Override
            public void run() {
                boolean ok;
                try {
                    Api.call("POST", core.serverUrl, core.token, "/device/attendant", new JSONObject().put("name", name));
                    ok = true;
                } catch (Exception e) {
                    Log.w(Core.TAG, "troca de atendente", e);
                    ok = false;
                }

                final boolean done = ok;
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        picking = false;
                        if (!done) {
                            toast("Sem conexão com a Central. Tente de novo.");
                        } else {
                            core.attendantName = name;
                            toast(name.isEmpty() ? "Celular devolvido." : "Olá, " + name + "!");
                            sendStatus();
                        }

                        updateLayout();
                        if (thenPick || core.attendantName.trim().isEmpty()) {
                            maybePickAttendant();
                        }
                    }
                });
            }
        }).start();
    }

    // ------------------------------------------------------------------ assistente de IA

    interface ScreenResult {
        void done(Object screen);
    }

    String serverUrl() {
        return core.serverUrl;
    }

    String token() {
        return core.token;
    }

    String hostname() {
        return core.hostname();
    }

    private void openAssistant() {
        if (assistant == null) {
            assistant = new Assistant(this, root);
        }

        assistant.show();
        updateLayout();
    }

    JSONArray assistantApps() {
        JSONArray apps = new JSONArray();
        for (Core.Item item : core.items) {
            try {
                apps.put(new JSONObject().put("name", item.name).put("kind", item.isProgram() ? "program" : "site"));
            } catch (JSONException ignored) {
                // Nome inválido: fica de fora.
            }
        }

        return apps;
    }

    private static final String SCREEN_SCRIPT = "(function(){try{var c=function(s,n){s=String(s||'').replace(/[ \\t]+/g,' ').trim();return s.length<=n?s:s.slice(0,n);};"
            + "return {address:location.host+location.pathname,title:c(document.title,200),text:c(document.body?document.body.innerText:'',9000)};}catch(e){return null;}})()";

    /** O que está na tela para a IA: o texto do site aberto. O WhatsApp (aplicativo) o Android não deixa ler. */
    void assistantScreen(boolean wantsScreen, final ScreenResult result) {
        final Tab tab = active != null && active.blockedHost == null ? active : null;
        if (!wantsScreen || tab == null) {
            JSONObject screen = null;
            if (wantsScreen) {
                try {
                    String open = TextUtils.join(", ", core.tabsInUse);
                    screen = new JSONObject().put("parts", new JSONArray().put(new JSONObject().put("kind", "home").put("active", true)
                            .put("name", "Tela inicial").put("address", "").put("title", "").put("contact", "")
                            .put("text", open.isEmpty() ? "Tela inicial do Terminal no celular." : "Tela inicial do Terminal no celular. Abertos: " + open + ".")));
                } catch (JSONException ignored) {
                    screen = null;
                }
            }

            result.done(screen);
            return;
        }

        tab.view.evaluateJavascript(SCREEN_SCRIPT, new ValueCallback<String>() {
            @Override
            public void onReceiveValue(String json) {
                JSONObject screen = null;
                try {
                    JSONObject o = json == null || json.equals("null") ? new JSONObject() : new JSONObject(json);
                    screen = new JSONObject().put("parts", new JSONArray().put(new JSONObject().put("kind", "page").put("active", true)
                            .put("name", tab.item.name).put("address", Core.str(o, "address")).put("title", Core.str(o, "title"))
                            .put("contact", "").put("text", Core.str(o, "text"))));
                } catch (JSONException e) {
                    Log.w(Core.TAG, "tela para a assistente", e);
                }

                result.done(screen);
            }
        });
    }

    private static String fold(String text) {
        String d = java.text.Normalizer.normalize(text.trim().toLowerCase(Locale.ROOT), java.text.Normalizer.Form.NFD);
        return d.replaceAll("\\p{M}", "");
    }

    /** A IA pediu para abrir um item liberado (pelo nome) ou pesquisar no Google. Devolve {"ok"|"erro", mensagem}. */
    String[] assistantAction(String action, String value) {
        if (action.equals("search")) {
            String address = "https://www.google.com/search?q=" + Uri.encode(value.trim());
            Core.Item google = null;
            for (Core.Item i : core.items) {
                String host = Uri.parse(i.target).getHost();
                if (!i.isProgram() && host != null && host.matches("(www\\.)?google\\.com(\\.br)?")) {
                    google = i;
                    break;
                }
            }

            if (google == null || core.policy.evaluate(address).verdict != HostPolicy.ALLOWED) {
                return new String[]{"erro", "O Google não está liberado neste celular. O TI pode liberar na Central."};
            }

            assistant.hide();
            for (Tab tab : tabs) {
                if (tab.item.id.equals(google.id)) {
                    activate(tab);
                    tab.view.loadUrl(address);
                    return new String[]{"ok", "Pesquisei no Google: " + value.trim()};
                }
            }

            Core.Item copy = new Core.Item();
            copy.id = google.id;
            copy.name = google.name;
            copy.kind = google.kind;
            copy.group = google.group;
            copy.hosts = google.hosts;
            copy.target = address;
            openSite(copy);
            return new String[]{"ok", "Pesquisei no Google: " + value.trim()};
        }

        String wanted = fold(value);
        Core.Item found = null;
        int close = 0;
        Core.Item closeItem = null;
        for (Core.Item i : core.items) {
            String n = fold(i.name);
            if (n.equals(wanted)) {
                found = i;
                break;
            }

            if (!wanted.isEmpty() && (n.contains(wanted) || wanted.contains(n))) {
                close++;
                closeItem = i;
            }
        }

        if (found == null && close == 1) {
            found = closeItem;
        }

        if (found == null) {
            return new String[]{"erro", "\"" + value + "\" não está liberado neste celular. O TI pode liberar na Central."};
        }

        if (found.isProgram() && !isWhatsApp(found)) {
            return new String[]{"erro", found.name + " é um programa de computador e não abre no celular."};
        }

        assistant.hide();
        openItem(found.id);
        updateLayout();
        return new String[]{"ok", "Abri " + found.name + "."};
    }

    // ------------------------------------------------------------------ gravação da tela

    private void maybeAskRecording() {
        if (!resumed || askingRecording || picking || RecorderService.running || !RecorderService.policyRecord
                || !RecorderService.withinHours() || !Core.STATE_ACTIVE.equals(core.state)
                || core.attendantName.trim().isEmpty() || Build.VERSION.SDK_INT < 26
                || SystemClock.elapsedRealtime() - askedRecordingAt < 10 * 60 * 1000 && askedRecordingAt != 0) {
            return;
        }

        askingRecording = true;
        askedRecordingAt = SystemClock.elapsedRealtime();
        try {
            MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
            Intent ask = Build.VERSION.SDK_INT >= 34
                    ? mpm.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
                    : mpm.createScreenCaptureIntent();
            toast("A Pontual grava a tela deste celular no horário de trabalho. Toque em \"Começar agora\".");
            startActivityForResult(ask, REQUEST_RECORDING);
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "pedido de gravação", e);
            askingRecording = false;
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQUEST_RECORDING) {
            return;
        }

        askingRecording = false;
        if (resultCode != RESULT_OK || data == null) {
            return;
        }

        try {
            startForegroundService(new Intent(this, RecorderService.class)
                    .putExtra(RecorderService.EXTRA_CODE, resultCode).putExtra(RecorderService.EXTRA_DATA, data));
        } catch (RuntimeException e) {
            Log.w(Core.TAG, "iniciar a gravação", e);
        }

        updateLayout();
    }

    // ------------------------------------------------------------------ atualização

    private void startUpdate() {
        confirm("Procurar e instalar a versão nova do Terminal agora?", new Runnable() {
            @Override
            public void run() {
                toast("Procurando atualização…");
                Updater.run(MainActivity.this, core.serverUrl, new Updater.Done() {
                    @Override
                    public void message(String text) {
                        toast(text);
                    }
                });
            }
        });
    }
}
