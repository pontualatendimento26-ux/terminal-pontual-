package br.com.pontualconsig.terminal;

import android.annotation.SuppressLint;
import android.net.Uri;
import android.util.Log;
import android.view.View;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

/**
 * Assistente de IA no celular, como a do PC (MainWindow.Assistant): um botão redondo no canto e, ao tocar, a conversa
 * (a página /terminal/assistente.html da Central) por cima da tela. A ponte é a mesma do PC (window.chrome.webview),
 * colocada na frente do assistente.js. A chave da IA nunca chega ao celular.
 * Diferença: o WhatsApp do celular é um aplicativo, e o Android não deixa ler a conversa dele; a IA vê só os sites
 * abertos no Terminal.
 */
final class Assistant {
    private final MainActivity activity;
    private final FrameLayout parent;
    private WebView view;

    Assistant(MainActivity activity, FrameLayout parent) {
        this.activity = activity;
        this.parent = parent;
    }

    boolean isOpen() {
        return view != null && view.getVisibility() == View.VISIBLE;
    }

    void hide() {
        if (view != null) {
            view.setVisibility(View.GONE);
        }
    }

    /** Fecha de vez (troca de atendente ou fim): a próxima abertura começa do zero. */
    void destroy() {
        if (view != null) {
            parent.removeView(view);
            view.destroy();
            view = null;
        }
    }

    private String url() {
        return activity.serverUrl() + "/terminal/assistente.html";
    }

    private boolean isMine(String url) {
        return url != null && (url.equals(url()) || url.startsWith(url() + "?") || url.startsWith(url() + "#"));
    }

    @SuppressLint({"SetJavaScriptEnabled", "AddJavascriptInterface"})
    void show() {
        if (view != null) {
            view.setVisibility(View.VISIBLE);
            view.bringToFront();
            send(new JSONObject(map("type", "assistant-shown")));
            return;
        }

        view = new WebView(activity);
        WebSettings s = view.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setSupportMultipleWindows(false);
        s.setGeolocationEnabled(false);
        view.addJavascriptInterface(new Bridge(), "PontualAndroid");
        view.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView v, WebResourceRequest request) {
                // A conversa nunca sai da própria página.
                return !isMine(request.getUrl().toString());
            }

            @Override
            public WebResourceResponse shouldInterceptRequest(WebView v, WebResourceRequest request) {
                Uri u = request.getUrl();
                String base = activity.serverUrl();
                if (request.isForMainFrame() || !"GET".equals(request.getMethod()) || base == null
                        || !"/terminal/assistente.js".equals(u.getPath()) || !u.toString().startsWith(base + "/")) {
                    return null;
                }

                byte[] script;
                try {
                    byte[] original = Api.fetch(base + "/terminal/assistente.js");
                    int skip = original.length >= 3 && (original[0] & 0xFF) == 0xEF ? 3 : 0;
                    byte[] before = prelude().getBytes(StandardCharsets.UTF_8);
                    script = new byte[before.length + original.length - skip];
                    System.arraycopy(before, 0, script, 0, before.length);
                    System.arraycopy(original, skip, script, before.length, original.length - skip);
                } catch (IOException e) {
                    script = new byte[0];
                }

                WebResourceResponse response = new WebResourceResponse("application/javascript", "utf-8", new ByteArrayInputStream(script));
                Map<String, String> headers = new HashMap<>();
                headers.put("Cache-Control", "no-store");
                response.setResponseHeaders(headers);
                return response;
            }
        });
        parent.addView(view, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        view.loadUrl(url());
    }

    private String prelude() {
        JSONObject info = new JSONObject();
        try {
            info.put("token", activity.token());
            info.put("hostname", activity.hostname());
            info.put("hosted", true);
            info.put("screen", true);
            info.put("actions", true);
        } catch (JSONException e) {
            Log.w(Core.TAG, "ponte da assistente", e);
        }

        return "(function(){if(window.top!==window)return;var L=[];"
                + "window.__pontualReceber=function(m){for(var i=0;i<L.length;i++){try{L[i]({data:m});}catch(e){}}};"
                + "var wv={postMessage:function(m){try{PontualAndroid.post(JSON.stringify(m));}catch(e){}},"
                + "addEventListener:function(t,f){if(t==='message')L.push(f);},removeEventListener:function(){}};"
                + "if(!window.chrome)window.chrome={};window.chrome.webview=wv;"
                + "Object.defineProperty(window,'PONTUAL',{value:Object.freeze(" + info + "),writable:false,configurable:false});"
                + "})();\n";
    }

    void send(JSONObject message) {
        if (view != null) {
            view.evaluateJavascript("window.__pontualReceber&&window.__pontualReceber(" + message + ");", null);
        }
    }

    private static Map<String, Object> map(String k, Object v) {
        Map<String, Object> m = new HashMap<>();
        m.put(k, v);
        return m;
    }

    private void onMessage(String json) {
        if (view == null || !isMine(view.getUrl())) {
            return;
        }

        try {
            JSONObject m = new JSONObject(json);
            String type = Core.str(m, "type");
            final String id = Core.str(m, "id");
            if (type.equals("assistant-close")) {
                hide();
            } else if (type.equals("assistant-context") && !id.isEmpty() && id.length() <= 40) {
                final boolean wantsScreen = m.optBoolean("screen", true);
                activity.assistantScreen(wantsScreen, new MainActivity.ScreenResult() {
                    @Override
                    public void done(Object screen) {
                        try {
                            send(new JSONObject().put("type", "assistant-context-result").put("id", id)
                                    .put("screen", screen == null ? JSONObject.NULL : screen).put("apps", activity.assistantApps()));
                        } catch (JSONException e) {
                            Log.w(Core.TAG, "tela para a assistente", e);
                        }
                    }
                });
            } else if (type.equals("assistant-action") && !id.isEmpty() && id.length() <= 40) {
                String action = Core.str(m, "action");
                String value = Core.str(m, "value");
                if ((action.equals("open") || action.equals("search")) && !value.isEmpty() && value.length() <= 200) {
                    String[] result = activity.assistantAction(action, value);
                    send(new JSONObject().put("type", "assistant-action-result").put("id", id)
                            .put("ok", "ok".equals(result[0])).put("message", result[1]));
                }
            }
        } catch (JSONException e) {
            Log.w(Core.TAG, "mensagem da assistente", e);
        }
    }

    private final class Bridge {
        @JavascriptInterface
        public void post(final String json) {
            activity.runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    onMessage(json);
                }
            });
        }
    }
}
