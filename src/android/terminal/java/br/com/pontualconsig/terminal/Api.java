package br.com.pontualconsig.terminal;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.Proxy;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/** Rotas do terminal (docs/V1-CONTRATO.md, seção 5). Só é chamada fora da linha de execução da tela. */
final class Api {
    /** O servidor respondeu, mas com erro. */
    static final class HttpError extends Exception {
        final int status;

        HttpError(int status, String message) {
            super(message);
            this.status = status;
        }
    }

    private Api() {
    }

    /** Devolve o corpo JSON da resposta, ou null quando a resposta não tem corpo (204). */
    static JSONObject call(String method, String serverUrl, String token, String path, JSONObject body)
            throws IOException, HttpError, JSONException {
        // O servidor fica na rede local: não passa pelo proxy configurado no aparelho.
        HttpURLConnection c = (HttpURLConnection) new URL(serverUrl + "/api/v1" + path).openConnection(Proxy.NO_PROXY);
        try {
            c.setRequestMethod(method);
            c.setConnectTimeout(5000);
            c.setReadTimeout(10000);
            c.setUseCaches(false);
            c.setInstanceFollowRedirects(false);
            c.setRequestProperty("Accept", "application/json");
            if (token != null && !token.isEmpty()) {
                c.setRequestProperty("Authorization", "Bearer " + token);
            }

            if (body != null) {
                byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
                c.setDoOutput(true);
                c.setRequestProperty("Content-Type", "application/json");
                c.setFixedLengthStreamingMode(bytes.length);
                OutputStream out = c.getOutputStream();
                try {
                    out.write(bytes);
                } finally {
                    out.close();
                }
            }

            int status = c.getResponseCode();
            if (status >= 200 && status < 300) {
                if (status == 204) {
                    return null;
                }

                String text = new String(readAll(c.getInputStream()), StandardCharsets.UTF_8).trim();
                return text.isEmpty() ? null : new JSONObject(text);
            }

            String message = null;
            try {
                InputStream error = c.getErrorStream();
                if (error != null) {
                    JSONObject o = new JSONObject(new String(readAll(error), StandardCharsets.UTF_8));
                    if (!o.isNull("error")) {
                        message = o.optString("error", null);
                    }
                }
            } catch (JSONException | IOException ignored) {
                // Erro sem corpo JSON (página de erro de um proxy, por exemplo).
            }

            throw new HttpError(status, message == null || message.trim().isEmpty()
                    ? "O servidor recusou o pedido (erro " + status + ")." : message);
        } finally {
            c.disconnect();
        }
    }

    /** Baixa um arquivo da tela inicial (usado para entregar o app.js com a ponte na frente). */
    static byte[] fetch(String url) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection(Proxy.NO_PROXY);
        try {
            c.setConnectTimeout(5000);
            c.setReadTimeout(10000);
            c.setUseCaches(false);
            c.setInstanceFollowRedirects(false);
            int status = c.getResponseCode();
            if (status != 200) {
                throw new IOException("HTTP " + status);
            }

            return readAll(c.getInputStream());
        } finally {
            c.disconnect();
        }
    }

    private static byte[] readAll(InputStream in) throws IOException {
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buffer = new byte[8192];
            int n;
            while ((n = in.read(buffer)) > 0) {
                out.write(buffer, 0, n);
                if (out.size() > 4 * 1024 * 1024) {
                    throw new IOException("Resposta grande demais.");
                }
            }

            return out.toByteArray();
        } finally {
            in.close();
        }
    }
}
