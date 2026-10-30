import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Card } from "../components/Card";
import {
  ErrorList,
  primaryButtonClass,
  secondaryButtonClass,
} from "../components/Field";
import { ApiError, apiDownload, apiFetch } from "../lib/api";
import type { Contact } from "./contacts";

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError
    ? error.messages
    : error
      ? ["Something went wrong. Please try again."]
      : [];
}

function sanitise(value: string): string {
  return (
    value
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/[\s_-]+/g, "-")
      .replace(/[^A-Za-z0-9-]/g, "")
      .replace(/^-+|-+$/g, "")
      .slice(0, 40) || "Unknown"
  );
}

/** The zip's file name. An erased contact appears under the pseudonym only. */
export function exportFileName(contact: Contact): string {
  return contact.isPseudonymised
    ? `Party-${sanitise(contact.lastName)}_data-export.zip`
    : `${sanitise(contact.lastName)}_${sanitise(contact.firstName)}_data-export.zip`;
}

/** GDPR data subject requests: export for staff, erasure (with a confirmation step) for Admins. */
export function GdprPanel({
  contact,
  canErase,
}: {
  contact: Contact;
  canErase: boolean;
}) {
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const [acknowledged, setAcknowledged] = useState(false);

  const exportData = useMutation({
    mutationFn: async () => {
      const blob = await apiDownload(`/api/gdpr/contacts/${contact.id}/export`);
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = exportFileName(contact);
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    },
  });

  const erase = useMutation({
    mutationFn: () =>
      apiFetch<unknown>(`/api/gdpr/contacts/${contact.id}/erase`, {
        method: "POST",
        body: { confirm: true },
      }),
    onSuccess: async () => {
      setConfirming(false);
      setAcknowledged(false);
      await queryClient.invalidateQueries({ queryKey: ["contacts"] });
      await queryClient.invalidateQueries({ queryKey: ["contact"] });
    },
  });

  const erased = contact.isPseudonymised;

  return (
    <Card title="Data protection (GDPR)">
      <div className="space-y-4 text-sm text-ink">
        <p>
          Export everything held about this contact (details, invites, items,
          field values, messages and files) as a zip. The export is recorded in
          the audit log.
        </p>
        <button
          type="button"
          className={secondaryButtonClass}
          disabled={exportData.isPending}
          onClick={() => exportData.mutate()}
        >
          {exportData.isPending ? "Preparing export..." : "Export data"}
        </button>
        <ErrorList messages={messagesOf(exportData.error)} />

        {canErase && (
          <div className="space-y-3 border-t border-surface-border pt-4">
            {erased ? (
              <p role="status">This contact's personal data has been erased.</p>
            ) : !confirming ? (
              <>
                <p>
                  Erasure deletes the contact's files, clears the answers, notes
                  and messages they provided, replaces their name, email and
                  phone with a pseudonym and revokes their links. The audit log
                  keeps its records.
                </p>
                <button
                  type="button"
                  className={secondaryButtonClass}
                  onClick={() => setConfirming(true)}
                >
                  Erase data...
                </button>
              </>
            ) : (
              <div
                role="alertdialog"
                aria-label="Confirm erasure"
                className="space-y-3 rounded-lg border border-danger-200 bg-danger-50 p-3"
              >
                <p className="font-medium text-danger-700">
                  This cannot be undone.
                </p>
                <label className="flex items-start gap-2">
                  <input
                    type="checkbox"
                    className="mt-1"
                    checked={acknowledged}
                    onChange={(e) => setAcknowledged(e.target.checked)}
                  />
                  <span>
                    I confirm that all personal data of this contact should be
                    permanently erased.
                  </span>
                </label>
                <div className="flex flex-wrap gap-3">
                  <button
                    type="button"
                    className={primaryButtonClass}
                    disabled={!acknowledged || erase.isPending}
                    onClick={() => erase.mutate()}
                  >
                    Erase permanently
                  </button>
                  <button
                    type="button"
                    className={secondaryButtonClass}
                    onClick={() => {
                      setConfirming(false);
                      setAcknowledged(false);
                    }}
                  >
                    Cancel
                  </button>
                </div>
              </div>
            )}
            <ErrorList messages={messagesOf(erase.error)} />
          </div>
        )}
      </div>
    </Card>
  );
}
