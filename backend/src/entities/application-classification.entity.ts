import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
  CreateDateColumn,
  UpdateDateColumn,
  Index,
} from 'typeorm';

/**
 * Application Classification Entity
 * 
 * Rules for classifying applications as WORK, NON_WORK, or NEUTRAL.
 * Used by Desktop Agents to classify application usage.
 */
@Entity('application_classifications')
@Index('idx_classifications_active', ['isActive'])
export class ApplicationClassification {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'name_pattern', type: 'varchar', length: 255, nullable: true })
  namePattern?: string; // e.g., "Chrome*", "*chrome.exe"

  @Column({ name: 'path_pattern', type: 'varchar', length: 500, nullable: true })
  pathPattern?: string; // e.g., "C:\\Program Files\\Google\\Chrome\\*"

  @Column({ name: 'window_title_pattern', type: 'varchar', length: 500, nullable: true })
  windowTitlePattern?: string; // e.g., "*Dashboard*"

  @Column({
    name: 'classification',
    type: 'varchar',
    length: 20,
  })
  classification: string; // WORK, NON_WORK, NEUTRAL

  @Column({ name: 'priority', type: 'int', default: 10 })
  priority: number; // Higher priority = checked first

  @Column({ name: 'is_active', type: 'boolean', default: true })
  isActive: boolean;

  @CreateDateColumn({ name: 'created_at' })
  createdAt: Date;

  @UpdateDateColumn({ name: 'updated_at' })
  updatedAt: Date;
}

