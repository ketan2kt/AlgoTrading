import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { ChangeDetectorRef, Component, inject, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { timeout } from 'rxjs';
import { AuthService, CurrentUser } from './auth.service';

interface AdminPage<T> { total: number; items: T[] }
interface AdminUsersPage extends AdminPage<AdminUser> { pending: number }
interface AdminUser {
  id: string;
  username: string;
  email: string;
  createdAtUtc: string;
  isActive: boolean;
  emailConfirmed: boolean;
  roles: string[];
}
interface ContactInquiry {
  id: string;
  name: string;
  email: string;
  company: string | null;
  service: string;
  message: string;
  deliveryStatus: string;
  deliveryAttempts: number;
  createdAtUtc: string;
  deliveredAtUtc: string | null;
}
type AdminSection = 'overview' | 'inquiries' | 'users';

@Component({
  selector: 'app-admin-panel',
  imports: [DatePipe, FormsModule],
  templateUrl: './admin-panel.component.html',
  styleUrl: './admin-panel.component.scss',
})
export class AdminPanelComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly view = inject(ChangeDetectorRef);

  protected user: CurrentUser | null = null;
  protected checking = true;
  protected loginBusy = false;
  protected loginError = '';
  protected username = '';
  protected password = '';
  protected section: AdminSection = 'overview';
  protected inquiries: ContactInquiry[] = [];
  protected inquiryTotal = 0;
  protected inquiriesLoading = false;
  protected inquiriesError = '';
  protected inquirySearch = '';
  protected users: AdminUser[] = [];
  protected userTotal = 0;
  protected pendingUserTotal = 0;
  protected usersLoading = false;
  protected usersError = '';
  protected userSearch = '';
  protected showAddUser = false;
  protected newUserEmail = '';
  protected userSaveBusy = false;
  protected userSaveError = '';
  protected notice = '';
  protected logoutBusy = false;

  ngOnInit(): void {
    this.auth.currentUser().pipe(timeout({ first: 10_000 })).subscribe({
      next: user => {
        this.user = user;
        this.checking = false;
        if (user) this.loadAll();
        this.view.markForCheck();
      },
      error: response => {
        this.checking = false;
        this.loginError = response.status === 403
          ? 'This account does not have administrator access.'
          : 'We could not verify the administrator session. Please refresh and try again.';
        this.view.markForCheck();
      },
    });
  }

  protected signIn(): void {
    if (!this.username.trim() || !this.password || this.loginBusy) return;
    this.loginBusy = true;
    this.loginError = '';
    this.auth.login(this.username.trim(), this.password).subscribe({
      next: user => {
        this.user = user;
        this.password = '';
        this.loginBusy = false;
        this.loadAll();
        this.view.markForCheck();
      },
      error: () => {
        this.password = '';
        this.loginBusy = false;
        this.loginError = 'Sign-in failed. Check your administrator credentials or account lockout status.';
        this.view.markForCheck();
      },
    });
  }

  protected signOut(): void {
    if (this.logoutBusy) return;
    this.logoutBusy = true;
    this.auth.logout().subscribe({
      next: () => {
        this.user = null;
        this.users = [];
        this.inquiries = [];
        this.logoutBusy = false;
        this.view.markForCheck();
      },
      error: () => {
        this.logoutBusy = false;
        this.notice = 'Sign out could not be confirmed. Refresh the page to check the session.';
        this.view.markForCheck();
      },
    });
  }

  protected selectSection(section: AdminSection): void {
    this.section = section;
    this.notice = '';
  }

  protected refresh(): void {
    this.loadAll();
  }

  protected loadMoreInquiries(): void { this.loadInquiries(true); }
  protected loadMoreUsers(): void { this.loadUsers(true); }

  protected createPendingUser(): void {
    const email = this.newUserEmail.trim();
    if (!email || this.userSaveBusy) return;
    this.userSaveBusy = true;
    this.userSaveError = '';
    this.notice = '';
    this.http.get<{ token: string }>('/api/security/antiforgery-token').subscribe({
      next: ({ token }) => this.http.post<AdminUser>('/api/admin/users', { email }, {
        headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }),
      }).subscribe({
        next: created => {
          this.users = [created, ...this.users];
          this.userTotal++;
          this.pendingUserTotal++;
          this.newUserEmail = '';
          this.showAddUser = false;
          this.userSaveBusy = false;
          this.notice = 'User added as pending. Sign-in remains disabled until access is separately approved.';
          this.view.markForCheck();
        },
        error: (response: HttpErrorResponse) => {
          this.userSaveBusy = false;
          this.userSaveError = response.status === 409
            ? 'An account with that email already exists.'
            : response.error?.message ?? 'The pending account could not be created.';
          this.view.markForCheck();
        },
      }),
      error: () => {
        this.userSaveBusy = false;
        this.userSaveError = 'We could not prepare a secure request. Refresh and try again.';
        this.view.markForCheck();
      },
    });
  }

  protected get visibleUsers(): AdminUser[] {
    const query = this.userSearch.trim().toLowerCase();
    if (!query) return this.users;
    return this.users.filter(user => `${user.username} ${user.email} ${user.roles.join(' ')}`.toLowerCase().includes(query));
  }

  protected get visibleInquiries(): ContactInquiry[] {
    const query = this.inquirySearch.trim().toLowerCase();
    if (!query) return this.inquiries;
    return this.inquiries.filter(inquiry =>
      `${inquiry.name} ${inquiry.email} ${inquiry.company ?? ''} ${inquiry.service} ${inquiry.message}`
        .toLowerCase().includes(query));
  }

  protected trackById(_index: number, item: { id: string }): string { return item.id; }

  private loadAll(): void {
    this.loadUsers();
    this.loadInquiries();
  }

  private loadUsers(append = false): void {
    this.usersLoading = true;
    this.usersError = '';
    const skip = append ? this.users.length : 0;
    this.http.get<AdminUsersPage>('/api/admin/users', { params: { skip, take: 100 } }).subscribe({
      next: page => { this.users = append ? [...this.users, ...page.items] : page.items; this.userTotal = page.total; this.pendingUserTotal = page.pending; this.usersLoading = false; this.view.markForCheck(); },
      error: () => { this.usersError = 'User accounts could not be loaded.'; this.usersLoading = false; this.view.markForCheck(); },
    });
  }

  private loadInquiries(append = false): void {
    this.inquiriesLoading = true;
    this.inquiriesError = '';
    const skip = append ? this.inquiries.length : 0;
    this.http.get<AdminPage<ContactInquiry>>('/api/admin/contact-inquiries', { params: { skip, take: 100 } }).subscribe({
      next: page => { this.inquiries = append ? [...this.inquiries, ...page.items] : page.items; this.inquiryTotal = page.total; this.inquiriesLoading = false; this.view.markForCheck(); },
      error: () => { this.inquiriesError = 'Contact enquiries could not be loaded.'; this.inquiriesLoading = false; this.view.markForCheck(); },
    });
  }
}
