using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Linq;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

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
                    }
                    else
                    {
                        blender_path_label.Text = "Invalid version selected";
                    }
                }
            }
            if (blender_path == null)
            {
                blender_path_label.Text = "Please provide Blender executive path; minimum version 4.0";
            }

            diffuse_button.Checked = true;
        }
        private void Convert()
        {
            if ((blender_path != "") && (fbx_path != ""))
            {
                string? working_path = System.IO.Path.GetDirectoryName(fbx_path);
                string prefix = fbx_path.Split(working_path + "\\")[1].Split(".fbx")[0];

                // Segment for normalizing
                string input_file = working_path + "\\mt_" + prefix + "_a_n.png";
                string output_file = working_path + "\\generated_displacement.png";

                // Set up displacement deformation
                CheckBox? cb = this.Controls["displacementDeformCheckbox"] as CheckBox;
                bool normalizeStatus = false;
                if (cb.Checked == true)
                {
                    normalizeStatus = true;
                    NormalToDisplacement.NormalConvert(input_file, output_file);
                    Debug.WriteLine("Conversion finished via external call.");
                    // End displacement
                }

                Debug.WriteLine("About to enter file check");
                if (File.Exists(working_path + "\\mt_" + prefix + "_a_" + panel1.Controls.OfType<RadioButton>().FirstOrDefault(r => r.Checked).Name.Substring(0, 1) + ".png"))
                {

                    string pythonFilePath = Path.Combine(Path.GetTempPath(), "ffxiv_fbx_to_fusion.py");
                    string python_working_path = working_path.Replace("\\", "\\\\")
                        .Replace("'", "\\'");

                    Debug.WriteLine("Writing first set of Python lines");
                    string[] lines =
                    {
                        "import bpy",
                        "import os",
                        "import numpy as np",
                        "import sys",
                        "",
                        // Escape problematic characters
                        "os.chdir('" + python_working_path + "')",
                        "",
                        // Import to Blender, then process
                        "# Import .fbx model",
                        "bpy.ops.import_scene.fbx(filepath=os.path.join(os.getcwd(), '" + prefix + "' + '.fbx'))",
                        "scene = bpy.context.scene",

                        // Processing
                        "objs = bpy.data.objects",
                        "objs.remove(objs['Cube'], do_unlink=True)",

                        "obj = bpy.data.objects.get('" + prefix + "')",

                        "if not obj:",
                        "    raise Exception(f\"Object '{" + prefix + "}' not found.\")",
                        "if obj is None:",
                        "    print(f\"Error: Object '{target_object_name}' not found.\")",

                        // Check for child objects
                        "if obj and obj.type == 'EMPTY':",
                            // Look for a child that is a mesh
                        "    mesh_obj = None",
                        "    for child in obj.children:",
                        "        if child.type == 'MESH':",
                        "            mesh_obj = child",
                        "            break",
                        "        for grandchild in child.children:",
                        "            if grandchild.type == 'MESH':",
                        "                mesh_obj = grandchild",
                        "                break",
                    };
                    if (normalizeStatus)
                    {
                        string[] normalize_lines = {
                            "IMAGE_PATH = '" + output_file.Replace("\\", "\\\\") + "'",
                            "OBJECT_NAME = '" + prefix + "'",
                            "SUBDIV_LEVEL = 4", // 4 is as high as can be done without causing massive problems with processing
                            // --- 0.0015 seems to be strongest without substantial negative impacts on geometry, but 0.004 seems like the "correct" value for when geometry is not an issue
                            "DISPLACE_STRENGTH = 0.0015",

                            // Make sure we are in object mode
                            "bpy.ops.object.mode_set(mode = 'OBJECT')",
                            "bpy.context.view_layer.objects.active = mesh_obj",
                            "mesh_obj.select_set(True)",

                            // 1. Add Subdivision Surface Modifier
                            "subsurf = mesh_obj.modifiers.new(name = 'Subdiv', type = 'SUBSURF')",
                            "subsurf.subdivision_type = 'SIMPLE'",
                            "subsurf.levels = SUBDIV_LEVEL",
                            "subsurf.render_levels = SUBDIV_LEVEL",

                            // 2. Add Displace Modifier
                            "displace = mesh_obj.modifiers.new(name = 'Displace', type = 'DISPLACE')",
                            "displace.direction = 'NORMAL'",
                            "displace.strength = DISPLACE_STRENGTH",

                            // 3. Create Texture
                            "tex = bpy.data.textures.new('NormalMapTex', type = 'IMAGE')",
                            "img = bpy.data.images.load(IMAGE_PATH)",
                            "tex.image = img",

                            // 4. Link Texture to Modifier
                            "displace.texture = tex",
                            "displace.texture_coords = 'UV'" };
                        lines = lines.Concat(normalize_lines).ToArray();
                    }
                    string[] suffix_lines = {
                        // Export to .obj format
                        "bpy.ops.wm.obj_export(filepath=os.path.join(os.getcwd(), '" + prefix + "' + '.obj'))",
                        "",
                        // Modify material file to be imported correctly
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
                    lines = lines.Concat(suffix_lines).ToArray();

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

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {

        }
    }
}