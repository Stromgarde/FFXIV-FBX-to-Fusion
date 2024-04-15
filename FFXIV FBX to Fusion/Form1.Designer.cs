namespace FFXIV_FBX_to_Fusion
{
    partial class FFXIV_FBX_to_Fusion
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            blender_location_button = new Button();
            convert_button = new Button();
            specular_button = new RadioButton();
            panel1 = new Panel();
            emissive_button = new RadioButton();
            opacity_button = new RadioButton();
            diffuse_button = new RadioButton();
            select_fbx_button = new Button();
            blender_path_label = new Label();
            fbx_path_label = new Label();
            debug_label = new Label();
            panel1.SuspendLayout();
            SuspendLayout();
            // 
            // blender_location_button
            // 
            blender_location_button.Location = new Point(125, 12);
            blender_location_button.Name = "blender_location_button";
            blender_location_button.Size = new Size(249, 23);
            blender_location_button.TabIndex = 0;
            blender_location_button.Text = "Select Blender Executable";
            blender_location_button.UseVisualStyleBackColor = true;
            blender_location_button.Click += blender_location_button_Click;
            // 
            // convert_button
            // 
            convert_button.Location = new Point(212, 126);
            convert_button.Name = "convert_button";
            convert_button.Size = new Size(75, 23);
            convert_button.TabIndex = 1;
            convert_button.Text = "Convert";
            convert_button.UseVisualStyleBackColor = true;
            convert_button.Click += convert_button_Click;
            // 
            // specular_button
            // 
            specular_button.AutoSize = true;
            specular_button.Location = new Point(21, 15);
            specular_button.Name = "specular_button";
            specular_button.Size = new Size(70, 19);
            specular_button.TabIndex = 2;
            specular_button.TabStop = true;
            specular_button.Text = "Specular";
            specular_button.UseVisualStyleBackColor = true;
            specular_button.CheckedChanged += radioButton1_CheckedChanged;
            // 
            // panel1
            // 
            panel1.Controls.Add(emissive_button);
            panel1.Controls.Add(opacity_button);
            panel1.Controls.Add(diffuse_button);
            panel1.Controls.Add(specular_button);
            panel1.Location = new Point(12, 12);
            panel1.Name = "panel1";
            panel1.Size = new Size(107, 137);
            panel1.TabIndex = 3;
            // 
            // emissive_button
            // 
            emissive_button.AutoSize = true;
            emissive_button.Location = new Point(21, 90);
            emissive_button.Name = "emissive_button";
            emissive_button.Size = new Size(70, 19);
            emissive_button.TabIndex = 5;
            emissive_button.TabStop = true;
            emissive_button.Text = "Emissive";
            emissive_button.UseVisualStyleBackColor = true;
            // 
            // opacity_button
            // 
            opacity_button.AutoSize = true;
            opacity_button.Location = new Point(21, 65);
            opacity_button.Name = "opacity_button";
            opacity_button.Size = new Size(66, 19);
            opacity_button.TabIndex = 4;
            opacity_button.TabStop = true;
            opacity_button.Text = "Opacity";
            opacity_button.UseVisualStyleBackColor = true;
            // 
            // diffuse_button
            // 
            diffuse_button.AutoSize = true;
            diffuse_button.Location = new Point(21, 40);
            diffuse_button.Name = "diffuse_button";
            diffuse_button.Size = new Size(62, 19);
            diffuse_button.TabIndex = 3;
            diffuse_button.TabStop = true;
            diffuse_button.Text = "Diffuse";
            diffuse_button.UseVisualStyleBackColor = true;
            // 
            // select_fbx_button
            // 
            select_fbx_button.Location = new Point(125, 73);
            select_fbx_button.Name = "select_fbx_button";
            select_fbx_button.Size = new Size(249, 23);
            select_fbx_button.TabIndex = 4;
            select_fbx_button.Text = "Select FBX file";
            select_fbx_button.UseVisualStyleBackColor = true;
            select_fbx_button.Click += fbx_location_button_Click;
            // 
            // blender_path_label
            // 
            blender_path_label.AutoSize = true;
            blender_path_label.Location = new Point(125, 39);
            blender_path_label.Name = "blender_path_label";
            blender_path_label.Size = new Size(134, 15);
            blender_path_label.TabIndex = 5;
            blender_path_label.Text = "Blender Executable Path";
            blender_path_label.Click += blender_path_label_Click;
            // 
            // fbx_path_label
            // 
            fbx_path_label.AutoSize = true;
            fbx_path_label.Location = new Point(125, 99);
            fbx_path_label.Name = "fbx_path_label";
            fbx_path_label.Size = new Size(75, 15);
            fbx_path_label.TabIndex = 6;
            fbx_path_label.Text = "FBX File Path";
            // 
            // debug_label
            // 
            debug_label.AutoSize = true;
            debug_label.Location = new Point(35, 197);
            debug_label.MaximumSize = new Size(490, 0);
            debug_label.Name = "debug_label";
            debug_label.Size = new Size(71, 15);
            debug_label.TabIndex = 7;
            debug_label.Text = "debug_label";
            debug_label.Click += label1_Click;
            // 
            // FFXIV_FBX_to_Fusion
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(496, 468);
            Controls.Add(debug_label);
            Controls.Add(fbx_path_label);
            Controls.Add(blender_path_label);
            Controls.Add(select_fbx_button);
            Controls.Add(panel1);
            Controls.Add(convert_button);
            Controls.Add(blender_location_button);
            Name = "FFXIV_FBX_to_Fusion";
            Text = "Form1";
            Load += Form1_Load;
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button blender_location_button;
        private Button convert_button;

        private Panel panel1;
        private RadioButton specular_button;
        private RadioButton emissive_button;
        private RadioButton opacity_button;
        private RadioButton diffuse_button;
        private Button select_fbx_button;
        private Label blender_path_label;
        private Label fbx_path_label;
        private Label debug_label;
    }
}