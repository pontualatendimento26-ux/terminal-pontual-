package br.com.pontualconsig.terminal;

import android.net.Uri;

import java.net.IDN;
import java.util.Collection;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;

/**
 * Política de navegação da página principal de cada site aberto (docs/V1-CONTRATO.md, "Modo de observação").
 * Mesma regra do programa do PC (Pontual.Terminal.Ipc.HostPolicy): um domínio é permitido se for igual a um
 * dos hosts de qualquer item do aparelho, ou subdomínio dele.
 */
final class HostPolicy {
    static final int ALLOWED = 0;
    static final int NOT_LISTED = 1;
    static final int SCHEME_BLOCKED = 2;

    static final HostPolicy EMPTY = new HostPolicy(new HashSet<String>());

    static final class Decision {
        final int verdict;
        final String host;

        Decision(int verdict, String host) {
            this.verdict = verdict;
            this.host = host;
        }
    }

    private final Set<String> hosts = new HashSet<>();

    HostPolicy(Collection<String> allowedHosts) {
        for (String raw : allowedHosts) {
            String host = normalizeHost(raw);
            if (host != null) {
                hosts.add(host);
            }
        }
    }

    Decision evaluate(String url) {
        if (url == null || url.trim().isEmpty()) {
            return new Decision(SCHEME_BLOCKED, "");
        }

        String text = url.trim();
        String lower = text.toLowerCase(Locale.ROOT);
        if (lower.equals("about:blank") || lower.startsWith("about:blank#")) {
            return new Decision(ALLOWED, "");
        }

        Uri uri = Uri.parse(text);
        String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
        if (!scheme.equals("http") && !scheme.equals("https")) {
            // file:, javascript:, data:, intent:, tel:, mailto: e afins nunca abrem.
            return new Decision(SCHEME_BLOCKED, "");
        }

        // Endereço com usuário embutido ("http://banco.com@outro.com") é recusado: é um truque comum de disfarce.
        if (uri.getUserInfo() != null) {
            return new Decision(SCHEME_BLOCKED, "");
        }

        String host = clean(uri.getHost());
        if (host == null) {
            return new Decision(SCHEME_BLOCKED, "");
        }

        return new Decision(isHostAllowed(host) ? ALLOWED : NOT_LISTED, host);
    }

    /** Recebe um domínio já isolado (sem esquema nem caminho). */
    boolean isHostAllowed(String host) {
        String candidate = normalizeHost(host);
        if (candidate == null) {
            return false;
        }

        if (hosts.contains(candidate)) {
            return true;
        }

        // Endereço IP só vale por igualdade: não existe "subdomínio" de IP.
        if (isIpAddress(candidate)) {
            return false;
        }

        for (String allowed : hosts) {
            // Nome de um rótulo só ("localhost", "intranet") e IP não têm subdomínios.
            if (allowed.indexOf('.') < 0 || isIpAddress(allowed)) {
                continue;
            }

            // O ponto antes do sufixo é o que separa "x.safra.com.br" de "xsafra.com.br".
            if (candidate.length() > allowed.length() + 1
                    && candidate.endsWith(allowed)
                    && candidate.charAt(candidate.length() - allowed.length() - 1) == '.') {
                return true;
            }
        }

        return false;
    }

    /**
     * Reduz uma entrada de hosts ao domínio em minúsculas, sem porta, caminho ou ponto final.
     * Aceita também uma URL completa. Devolve null se não der para extrair um domínio.
     */
    static String normalizeHost(String raw) {
        if (raw == null || raw.trim().isEmpty()) {
            return null;
        }

        String text = raw.trim();
        if (!text.contains("://")) {
            text = "http://" + text;
        }

        Uri uri = Uri.parse(text);
        String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
        if ((!scheme.equals("http") && !scheme.equals("https")) || uri.getUserInfo() != null) {
            return null;
        }

        return clean(uri.getHost());
    }

    private static String clean(String host) {
        if (host == null) {
            return null;
        }

        String h = host.trim().toLowerCase(Locale.ROOT);
        while (h.endsWith(".")) {
            h = h.substring(0, h.length() - 1);
        }

        if (h.isEmpty()) {
            return null;
        }

        try {
            h = IDN.toASCII(h).toLowerCase(Locale.ROOT);
        } catch (IllegalArgumentException e) {
            return null;
        }

        return h.isEmpty() ? null : h;
    }

    private static boolean isIpAddress(String host) {
        return host.indexOf(':') >= 0 || host.matches("\\d{1,3}(\\.\\d{1,3}){3}");
    }
}
