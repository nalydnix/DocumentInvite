import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Route, Routes } from "react-router-dom";
import type { Role } from "../auth/AuthContext";
import {
  jsonResponse,
  mockApi,
  meBody,
  renderApp,
  resetSession,
  tokens,
} from "../test-utils";
import { ContactFormPage } from "./ContactFormPage";
import { exportFileName } from "./GdprPanel";
import type { Contact } from "./contacts";

afterEach(() => {
  vi.restoreAllMocks();
  resetSession();
});

const ann: Contact = {
  id: "c1",
  firstName: "Ann",
  lastName: "Lee",
  email: "ann@example.com",
  phone: null,
  partyType: "Borrower",
  isPseudonymised: false,
};

function signedIn(roles: Role[]) {
  sessionStorage.setItem("di.refreshToken", "refresh-1");
  return {
    "POST /api/auth/refresh": () => jsonResponse(200, tokens),
    "GET /api/me": () => jsonResponse(200, meBody(roles)),
  };
}

function renderContact() {
  return renderApp(
    <Routes>
      <Route path="/contacts/:id" element={<ContactFormPage />} />
    </Routes>,
    "/contacts/c1",
  );
}

describe("exportFileName", () => {
  it("uses the pseudonym for an erased contact", () => {
    expect(exportFileName(ann)).toBe("Lee_Ann_data-export.zip");
    expect(
      exportFileName({
        ...ann,
        firstName: "Erased",
        lastName: "7F3A",
        isPseudonymised: true,
      }),
    ).toBe("Party-7F3A_data-export.zip");
  });
});

describe("GdprPanel", () => {
  it("exports the contact data as a download", async () => {
    const createObjectURL = vi.fn(() => "blob:x");
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    vi.stubGlobal(
      "URL",
      Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }),
    );
    const { calls } = mockApi({
      ...signedIn(["LoanOfficer"]),
      "GET /api/contacts/c1": () => jsonResponse(200, ann),
      "GET /api/gdpr/contacts/c1/export": () =>
        new Response("zip", { status: 200 }),
    });
    renderContact();

    await userEvent.click(
      await screen.findByRole("button", { name: "Export data" }),
    );

    await waitFor(() => expect(createObjectURL).toHaveBeenCalled());
    expect(
      calls.some((c) => c.key === "GET /api/gdpr/contacts/c1/export"),
    ).toBe(true);
    expect(
      screen.queryByRole("button", { name: "Erase data..." }),
    ).not.toBeInTheDocument();
  });

  it("is not shown to a viewer", async () => {
    mockApi({
      ...signedIn(["Viewer"]),
      "GET /api/contacts/c1": () => jsonResponse(200, ann),
    });
    renderContact();

    expect(await screen.findByText("Contact details")).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Export data" }),
    ).not.toBeInTheDocument();
  });

  it("erases only after an explicit confirmation", async () => {
    let erased = false;
    const { calls } = mockApi({
      ...signedIn(["Admin"]),
      "GET /api/contacts/c1": () =>
        jsonResponse(
          200,
          erased
            ? {
                ...ann,
                firstName: "Erased",
                lastName: "7F3A",
                isPseudonymised: true,
              }
            : ann,
        ),
      "POST /api/gdpr/contacts/c1/erase": () => {
        erased = true;
        return jsonResponse(200, {});
      },
    });
    renderContact();
    const user = userEvent.setup();

    await user.click(
      await screen.findByRole("button", { name: "Erase data..." }),
    );
    const confirmButton = screen.getByRole("button", {
      name: "Erase permanently",
    });
    expect(confirmButton).toBeDisabled();
    await user.click(screen.getByRole("checkbox"));
    await user.click(confirmButton);

    expect(
      await screen.findByText("This contact's personal data has been erased."),
    ).toBeInTheDocument();
    const call = calls.find(
      (c) => c.key === "POST /api/gdpr/contacts/c1/erase",
    );
    expect(JSON.parse(String(call?.init?.body))).toEqual({ confirm: true });
  });
});
