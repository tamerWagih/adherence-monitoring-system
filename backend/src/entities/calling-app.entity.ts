import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
  CreateDateColumn,
  UpdateDateColumn,
  Index,
} from 'typeorm';

/**
 * Calling App Entity
 * 
 * Tracks VoIP/telephony applications used by agents.
 * Used by Desktop Agents to detect when agents use calling applications and track call status.
 */
@Entity('calling_apps')
@Index('idx_calling_apps_type', ['appType'])
@Index('idx_calling_apps_domain', ['domain'])
@Index('idx_calling_apps_process', ['processNamePattern'])
@Index('idx_calling_apps_active', ['isActive'])
@Index('idx_calling_apps_client', ['clientName'])
export class CallingApp {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'app_name', type: 'varchar', length: 255 })
  appName: string; // e.g., "Five9", "Salesforce", "Zoiper"

  @Column({ name: 'app_type', type: 'varchar', length: 50 })
  appType: 'WEB' | 'DESKTOP';

  @Column({ name: 'client_name', type: 'varchar', length: 255, nullable: true })
  clientName?: string; // Optional: which client uses this app

  // For web apps:
  @Column({ name: 'website_url', type: 'varchar', length: 500, nullable: true })
  websiteUrl?: string; // e.g., "https://*.salesforce.com/*"

  @Column({ name: 'domain', type: 'varchar', length: 255, nullable: true })
  domain?: string; // e.g., "salesforce.com"

  @Column({ name: 'url_pattern', type: 'varchar', length: 500, nullable: true })
  urlPattern?: string; // Optional pattern for matching (e.g., "*.salesforce.com")

  // For desktop apps:
  @Column({ name: 'process_name_pattern', type: 'varchar', length: 255, nullable: true })
  processNamePattern?: string; // e.g., "Five9*.exe", "zoiper.exe"

  @Column({ name: 'window_title_pattern', type: 'varchar', length: 500, nullable: true })
  windowTitlePattern?: string; // e.g., "*Five9*", "*Zoiper*"

  // Call status detection patterns
  @Column({ name: 'call_status_patterns', type: 'jsonb', nullable: true })
  callStatusPatterns?: {
    in_call?: string[];
    ringing?: string[];
    idle?: string[];
    on_hold?: string[];
  };

  @Column({ name: 'is_active', type: 'boolean', default: true })
  isActive: boolean;

  @CreateDateColumn({ name: 'created_at' })
  createdAt: Date;

  @UpdateDateColumn({ name: 'updated_at' })
  updatedAt: Date;
}

