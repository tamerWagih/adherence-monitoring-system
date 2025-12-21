import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
  CreateDateColumn,
  UpdateDateColumn,
  Index,
} from 'typeorm';

/**
 * Client Website Entity
 * 
 * Tracks client-specific websites that agents access during work.
 * Used by Desktop Agents to detect when agents access client websites.
 */
@Entity('client_websites')
@Index('idx_client_websites_domain', ['domain'])
@Index('idx_client_websites_client', ['clientName'])
@Index('idx_client_websites_active', ['isActive'])
export class ClientWebsite {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'client_name', type: 'varchar', length: 255 })
  clientName: string; // e.g., "OYO", "Instacart", "Netflix"

  @Column({ name: 'website_url', type: 'varchar', length: 500 })
  websiteUrl: string; // e.g., "https://*.oyorooms.com/*"

  @Column({ name: 'domain', type: 'varchar', length: 255 })
  domain: string; // e.g., "oyorooms.com"

  @Column({ name: 'url_pattern', type: 'varchar', length: 500, nullable: true })
  urlPattern?: string; // Optional pattern for matching (e.g., "*.five9.com")

  @Column({ name: 'is_active', type: 'boolean', default: true })
  isActive: boolean;

  @CreateDateColumn({ name: 'created_at' })
  createdAt: Date;

  @UpdateDateColumn({ name: 'updated_at' })
  updatedAt: Date;
}

