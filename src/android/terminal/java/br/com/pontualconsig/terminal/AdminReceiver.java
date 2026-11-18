package br.com.pontualconsig.terminal;

import android.app.admin.DeviceAdminReceiver;

/**
 * Componente que o Android exige para o app poder virar "dono do aparelho" (Device Owner).
 * Não faz nada sozinho: o bloqueio é aplicado e desfeito pela classe Lock.
 */
public class AdminReceiver extends DeviceAdminReceiver {
}
