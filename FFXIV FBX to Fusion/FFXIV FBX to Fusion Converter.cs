using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Collections;
using System.Security;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Management.Automation;
using System.Collections.ObjectModel;
using System.Text;
using System.Security.Cryptography;
using System.Management.Automation.Runspaces;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace FFXIV_FBX_to_Fusion
{
    public partial class FFXIV_FBX_to_Fusion : Form
    {
        private String blender_path = "";
        private String fbx_path = "";


        public FFXIV_FBX_to_Fusion()
        {
            InitializeComponent();
        }

        private void radioButton1_CheckedChanged(object sender, EventArgs e)
        {
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Attempt to determine find default location for Blender binary
            String[] drives = { "C:\\", "D:\\", "E:\\" };
            foreach (String drive in drives)
            {
                String temp_path = drive + "Program Files\\Blender Foundation\\";
                if (Directory.Exists(temp_path))
                {
                    String latest_path = Directory.GetDirectories(drive + "Program Files\\Blender Foundation\\")[Directory.GetDirectories(drive + "Program Files\\Blender Foundation\\").Length - 1];
                    float version_number = float.Parse(FileVersionInfo.GetVersionInfo(latest_path + "\\blender.exe").FileVersion);
                    if (version_number >= 4)
                    {
                        blender_path = latest_path + "\\blender.exe";
                        blender_path_label.Text = blender_path;
                    }
                }
                temp_path = drive + "Program Files (x86)\\Blender Foundation\\";
                if (Directory.Exists(temp_path))
                {
                    String latest_path = Directory.GetDirectories(drive + "Program Files\\Blender Foundation\\")[Directory.GetDirectories(drive + "Program Files\\Blender Foundation\\").Length - 1];
                    if (FileVersionInfo.GetVersionInfo(latest_path + "\\blender.exe").FileVersion != null)
                    {
                        float version_number = float.Parse(FileVersionInfo.GetVersionInfo(latest_path + "\\blender.exe").FileVersion);
                        if (version_number >= 4)
                        {
                            blender_path = latest_path + "\\blender.exe";
                            blender_path_label.Text = blender_path;
                        }
                    } else
                    {
                        blender_path_label.Text = "Invalid version selected";
                    }
                }
            }
            if (blender_path == null)
            {
                blender_path_label.Text = "Please provide Blender executive path; minimum version 4.0";
            }

            specular_button.Checked = true;
        }
        private void Convert()
        {
            if ((blender_path != "") && (fbx_path != ""))
            {
                string working_path = System.IO.Path.GetDirectoryName(fbx_path);
                string prefix = fbx_path.Split(working_path + "\\")[1].Split(".fbx")[0];

                if (File.Exists(working_path + "\\mt_" + prefix + "_a_" + panel1.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Checked).Name.Substring(0, 1) + ".png"))
                {

                    string pythonFilePath = Path.Combine(Path.GetTempPath(), "ffxiv_fbx_to_fusion.py");
                    string python_working_path = working_path.Replace("\\", "\\\\")
                        .Replace("'","\\'");

                    string[] lines =
                    {
                        "import bpy",
                        "import os",
                        "import sys",
                        "",
                        "objs = bpy.data.objects",
                        "objs.remove(objs['Cube'], do_unlink=True)",
                        "",
                        // Escape problematic characters
                        "os.chdir('" + python_working_path + "')",
                        "",
                        "# Import .fbx model",
                        "bpy.ops.import_scene.fbx(filepath=os.path.join(os.getcwd(), '" + prefix + "' + '.fbx'))",
                        // Export to .obj format
                        "bpy.ops.wm.obj_export(filepath=os.path.join(os.getcwd(), '" + prefix + "' + '.obj'))",
                        "",
                        "with open(os.path.join(os.getcwd(), '" + prefix + "' + '.mtl'), 'r') as file: ",
                        "  ",
                        //   Reading the content of the file
                        //   using the read() function and storing
                        //   them in a new variable
                        "    data = file.read() ",
                        "  ",
                        // Searching and replacing the text
                        // using the replace() function
                        "    data = data.replace('C:/', '') ",
                        "    data = data.replace('" + prefix + "' + '_a_d.png', '" + prefix + "' + '_a_' + '" + panel1.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Checked).Name.Substring(0, 1) + "' + '.png')",
                        "  ",
                        // Opening our text file in write only
                        // mode to write the replaced content
                        "with open(os.path.join(os.getcwd(), '" + prefix + "') + '.mtl', 'w') as file: ",
                        "  ",
                        //   Writing the replaced data in our
                        //   text file ",
                        "    file.write(data)"
                    };

                    using (StreamWriter outputFile = new StreamWriter(pythonFilePath))
                    {
                        foreach (string line in lines)
                            outputFile.WriteLine(line);
                    }

                    System.Diagnostics.Process process = new System.Diagnostics.Process();
                    System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();
                    startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                    startInfo.FileName = blender_path;
                    startInfo.Arguments = "-b -P \"" + pythonFilePath + "\"";
                    process.StartInfo = startInfo;
                    process.Start();

                    System.Windows.Forms.MessageBox.Show("Conversion complete");
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show("Textures not found");
                }
            }
            else
            {
                System.Windows.Forms.MessageBox.Show("Must supply FBX model file with corresponding textures");
            }
        }

        private void fbx_location_button_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog1 = new()
            {
                FileName = "",
                Filter = "FBX Model|*.fbx",
                Title = "Select FBX Model"
            };
            DialogResult result = openFileDialog1.ShowDialog(); // Show the dialog.
            if (result == DialogResult.OK) // Test result.
            {
                fbx_path = openFileDialog1.FileName;
                fbx_path_label.Text = fbx_path; // <-- Shows file size in debugging mode.
            }
        }

        private void convert_button_Click(object sender, EventArgs e)
        {
            Convert();
        }

        private void Form1_Load_1(object sender, EventArgs e)
        {

        }

        private void blender_location_button_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog1 = new()
            {
                FileName = "blender.exe",
                Filter = "Blender executable|blender.exe",
                Title = "Select Blender executable"
            };
            DialogResult result = openFileDialog1.ShowDialog(); // Show the dialog.
            if (result == DialogResult.OK) // Test result.
            {
                blender_path = openFileDialog1.FileName;
                blender_path_label.Text = blender_path; // <-- Shows file size in debugging mode.
            }
        }

        private void blender_path_label_Click(object sender, EventArgs e)
        {

        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {

        }

        private void label1_Click(object sender, EventArgs e)
        {

        }
    }
}